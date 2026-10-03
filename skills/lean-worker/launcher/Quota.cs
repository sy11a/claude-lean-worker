// Subscription quota: how much of a coding plan's usage windows is used. Read before a subscription-billed
// run (to pick a model with headroom) and after it (to record what the task used). Adapters per provider;
// z.ai's GLM Coding Plan is the one verified so far.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Quota
{
    /// <summary>
    /// Reads the provider's quota, using a cached reading younger than maxAge (for status lines).
    /// </summary>
    public static QuotaReading Read(Provider p, TimeSpan? maxAge = null)
    {
        string adapter = Json.Str(p.Quota, "adapter") ?? throw new LaunchException($"provider '{p.Name}' has no quota adapter in prices.json");
        string cache = Path.Combine(CacheDir(), $"quota-{p.Name}.json");
        if (maxAge is { } age && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < age)
        {
            try { return QuotaReading.FromJson(Json.ParseLenient(File.ReadAllText(cache)).AsObject()); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException) { }
        }
        QuotaReading reading = adapter switch
        {
            "zai" => ReadZai(p),
            "minimax" => ReadMinimax(p),
            _ => throw new LaunchException($"unknown quota adapter '{adapter}' (supported: zai, minimax)"),
        };
        _ = Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, reading.ToJson().ToJsonString(Json.Indented), Json.Utf8);
        return reading;
    }

    /// <summary>
    /// A model has headroom while every window is below its maxPercent (defaults: all windows below 95%).
    /// </summary>
    public static (bool Ok, string Why) Headroom(Provider p, QuotaReading q)
    {
        JsonObject? limits = p.Quota?["maxPercent"] as JsonObject;
        foreach (QuotaWindow w in q.Windows)
        {
            decimal? max = Json.Dec(limits, w.Name) ?? (limits is null ? 95 : null);
            if (max is { } m && w.Percent >= m)
            {
                return (false, string.Create(CultureInfo.InvariantCulture, $"{p.Name} {w.Name} {w.Percent:0.#}% >= {m:0.#}%"));
            }
        }
        return (true, $"{p.Name} {q.Line()}");
    }

    public static bool IsReadFailure(Exception ex)
    {
        return ex is LaunchException or HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException;
    }

    private static string CacheDir()
    {
        return Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? Path.Combine(x, "lean-worker")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "lean-worker");
    }

    // GET /v1/api/openplatform/coding_plan/remains (verified 2026-09-29, international region). A plan key works in one
    // region only: "region": "cn" in the provider's quota block uses api.minimaxi.com.
    private static QuotaReading ReadMinimax(Provider p)
    {
        string host = Json.Str(p.Quota, "region") is "cn" ? "https://api.minimaxi.com" : "https://api.minimax.io";
        string url = Json.Str(p.Quota, "url") ?? $"{host}/v1/api/openplatform/coding_plan/remains";
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Runtimes.ProviderKey(p));
        using HttpResponseMessage res = http.Send(req);
        string body = new StreamReader(res.Content.ReadAsStream()).ReadToEnd();
        if (!res.IsSuccessStatusCode)
        {
            throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"MiniMax quota: HTTP {(int)res.StatusCode}"));
        }

        return ParseMinimax(p.Name, body);
    }

    // One entry per model family: "general" (the text models workers use) and others such as "video". Each has an
    // interval window (5 h for general) and a weekly window. MiniMax reports what is LEFT: *_remaining_percent, and
    // the *_usage_count fields also count remaining requests. Windows are named by length; other models get a prefix.
    internal static QuotaReading ParseMinimax(string provider, string body)
    {
        JsonObject doc;
        try { doc = Json.ParseLenient(body).AsObject(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { throw new LaunchException("MiniMax quota: the response is not JSON"); }
        if (doc["base_resp"] is JsonObject br && Json.Num(br["status_code"]) != 0)
        {
            throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"MiniMax quota: {Json.Str(br, "status_msg") ?? "error"} ({Json.Num(br["status_code"])})"));
        }

        List<QuotaWindow> windows = [];
        foreach (JsonObject m in (doc["model_remains"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string model = Json.Str(m, "model_name") ?? "?";
            string prefix = model is "general" ? string.Empty : model + "-";
            // The text models' interval is the plan's 5-hour block. Blocks are clock-aligned and cut at the UTC day
            // boundary, so one can be shorter (22:00-02:00 CEST was 4 h); it is still the "5h" window.
            // Other families are named by their block length (video: 24h).
            long hours = (long)Math.Round((Json.Num(m["end_time"]) - Json.Num(m["start_time"])) / 3_600_000.0, MidpointRounding.ToEven);
            string interval;
            if (model is "general")
            {
                interval = "5h";
            }
            else if (hours > 0)
            {
                interval = string.Create(CultureInfo.InvariantCulture, $"{hours}h");
            }
            else
            {
                interval = "interval";
            }

            windows.Add(Window($"{prefix}{interval}", m, "current_interval", "end_time"));
            windows.Add(Window($"{prefix}weekly", m, "current_weekly", "weekly_end_time"));
        }
        if (windows.Count is 0)
        {
            throw new LaunchException("MiniMax quota: no model_remains in response");
        }

        return new QuotaReading(provider, Level: null, windows, DateTimeOffset.Now);

        static QuotaWindow Window(string name, JsonObject m, string field, string endField)
        {
            decimal remaining = Json.Dec(m, $"{field}_remaining_percent") ?? 100;
            long total = Json.Num(m[$"{field}_total_count"]);
            DateTimeOffset? reset = m[endField] is JsonValue v && v.TryGetValue(out long ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : null;
            return new QuotaWindow(name, 100 - remaining, reset, total > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Json.Num(m[$"{field}_usage_count"])} of {total} requests left") : null);
        }
    }

    // GET /api/monitor/usage/quota/limit (verified 2026-09-29). TOKENS_LIMIT windows carry a used percentage;
    // unit 3 = hours, 5 = months, 6 = weeks (inferred from nextResetTime). TIME_LIMIT counts MCP tool calls.
    private static QuotaReading ReadZai(Provider p)
    {
        string baseUrl = Json.Str(p.Quota, "url") ?? "https://api.z.ai/api/monitor/usage/quota/limit";
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
        using HttpRequestMessage req = new(HttpMethod.Get, baseUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Runtimes.ProviderKey(p));
        req.Headers.AcceptLanguage.ParseAdd("en-US");
        using HttpResponseMessage res = http.Send(req);
        string body = new StreamReader(res.Content.ReadAsStream()).ReadToEnd();
        if (!res.IsSuccessStatusCode)
        {
            throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"z.ai quota: HTTP {(int)res.StatusCode}"));
        }

        return ParseZai(p.Name, body);
    }

    internal static QuotaReading ParseZai(string provider, string body)
    {
        JsonObject data;
        try { data = Json.ParseLenient(body)["data"] as JsonObject ?? throw new LaunchException("z.ai quota: no data in response"); }
        catch (System.Text.Json.JsonException) { throw new LaunchException("z.ai quota: the response is not JSON"); }
        List<QuotaWindow> windows = [];
        foreach (JsonObject l in (data["limits"] as JsonArray ?? []).OfType<JsonObject>())
        {
            long unit = Json.Num(l["unit"]);
            long number = Json.Num(l["number"]);
            string span = unit switch
            {
                3 => string.Create(CultureInfo.InvariantCulture, $"{number}h"),
                5 => number == 1 ? "monthly" : string.Create(CultureInfo.InvariantCulture, $"{number}mo"),
                6 => number == 1 ? "weekly" : string.Create(CultureInfo.InvariantCulture, $"{number}w"),
                _ => string.Create(CultureInfo.InvariantCulture, $"u{unit}x{number}"),
            };
            string? type = Json.Str(l, "type");
            string name = type is "TIME_LIMIT" ? $"mcp-{span}" : span;
            DateTimeOffset? reset = l["nextResetTime"] is JsonValue r && r.TryGetValue(out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : null;
            string? detail = type is "TIME_LIMIT" ? string.Create(CultureInfo.InvariantCulture, $"{Json.Num(l["currentValue"])} of {Json.Num(l["usage"])} calls") : null;
            windows.Add(new QuotaWindow(name, Json.Dec(l, "percentage") ?? 0, reset, detail));
        }
        return new QuotaReading(provider, Json.Str(data, "level"), windows, DateTimeOffset.Now);
    }
}