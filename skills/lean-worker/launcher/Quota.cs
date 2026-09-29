// Subscription quota: how much of a coding plan's usage windows is used. Read before a subscription-billed
// run (to pick a model with headroom) and after it (to record what the task used). Adapters per provider;
// z.ai's GLM Coding Plan is the one verified so far.

using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed record QuotaWindow(string Name, decimal Percent, DateTimeOffset? ResetsAt, string? Detail);

internal sealed record QuotaReading(string Provider, string? Level, List<QuotaWindow> Windows, DateTimeOffset ReadAt)
{
    public JsonObject ToJson() => new()
    {
        ["provider"] = Provider,
        ["level"] = Level,
        ["read_at"] = ReadAt.ToString("o"),
        ["windows"] = new JsonArray(Windows.Select(w => (JsonNode)new JsonObject
        {
            ["name"] = w.Name, ["percent"] = w.Percent, ["resets_at"] = w.ResetsAt?.ToString("o"), ["detail"] = w.Detail,
        }).ToArray()),
    };

    public static QuotaReading FromJson(JsonObject o) => new(
        Json.Str(o, "provider") ?? "", Json.Str(o, "level"),
        (o["windows"] as JsonArray ?? []).OfType<JsonObject>().Select(w => new QuotaWindow(
            Json.Str(w, "name") ?? "", Json.Dec(w, "percent") ?? 0,
            Json.Str(w, "resets_at") is { } r ? DateTimeOffset.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : null,
            Json.Str(w, "detail"))).ToList(),
        DateTimeOffset.Parse(Json.Str(o, "read_at") ?? DateTimeOffset.MinValue.ToString("o"), System.Globalization.CultureInfo.InvariantCulture));

    public string Line(QuotaReading? before = null) => string.Join(", ", Windows.Select(w =>
    {
        var prev = before?.Windows.FirstOrDefault(b => b.Name == w.Name);
        var pct = prev is null || prev.Percent == w.Percent ? $"{w.Percent:0.#}%" : $"{prev.Percent:0.#}% -> {w.Percent:0.#}%";
        return $"{w.Name} {pct}{(w.ResetsAt is { } r ? $" (resets in {Until(r)})" : "")}";
    }));

    private static string Until(DateTimeOffset t)
    {
        var d = t - DateTimeOffset.Now;
        if (d < TimeSpan.Zero) return "0m";
        return d.TotalDays >= 1 ? $"{(int)d.TotalDays}d{d.Hours}h" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}m" : $"{d.Minutes}m";
    }
}

internal static class Quota
{
    /// <summary>Reads the provider's quota, using a cached reading younger than maxAge (for status lines).</summary>
    public static QuotaReading Read(Provider p, TimeSpan? maxAge = null)
    {
        var adapter = Json.Str(p.Quota, "adapter") ?? throw new LaunchException($"provider '{p.Name}' has no quota adapter in prices.json");
        var cache = Path.Combine(CacheDir(), $"quota-{p.Name}.json");
        if (maxAge is { } age && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < age)
        {
            try { return QuotaReading.FromJson(Json.ParseLenient(File.ReadAllText(cache)).AsObject()); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException) { }
        }
        var reading = adapter switch
        {
            "zai" => ReadZai(p),
            _ => throw new LaunchException($"unknown quota adapter '{adapter}' (supported: zai)"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, reading.ToJson().ToJsonString(Json.Indented), Json.Utf8);
        return reading;
    }

    /// <summary>A model has headroom while every window is below its maxPercent (defaults: all windows below 95%).</summary>
    public static (bool Ok, string Why) Headroom(Provider p, QuotaReading q)
    {
        var limits = p.Quota?["maxPercent"] as JsonObject;
        foreach (var w in q.Windows)
        {
            var max = Json.Dec(limits, w.Name) ?? (limits is null ? 95 : null);
            if (max is { } m && w.Percent >= m) return (false, $"{p.Name} {w.Name} {w.Percent:0.#}% >= {m:0.#}%");
        }
        return (true, $"{p.Name} {q.Line()}");
    }

    public static bool IsReadFailure(Exception ex) => ex is LaunchException or HttpRequestException or TaskCanceledException
        or System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException;

    private static string CacheDir() =>
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? Path.Combine(x, "lean-worker")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "lean-worker");

    // GET /api/monitor/usage/quota/limit (verified 2026-09-29). TOKENS_LIMIT windows carry a used percentage;
    // unit 3 = hours, 5 = months, 6 = weeks (inferred from nextResetTime). TIME_LIMIT counts MCP tool calls.
    private static QuotaReading ReadZai(Provider p)
    {
        var baseUrl = Json.Str(p.Quota, "url") ?? "https://api.z.ai/api/monitor/usage/quota/limit";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Runtimes.ProviderKey(p));
        req.Headers.AcceptLanguage.ParseAdd("en-US");
        using var res = http.Send(req);
        var body = new StreamReader(res.Content.ReadAsStream()).ReadToEnd();
        if (!res.IsSuccessStatusCode) throw new LaunchException($"z.ai quota: HTTP {(int)res.StatusCode}");
        return ParseZai(p.Name, body);
    }

    internal static QuotaReading ParseZai(string provider, string body)
    {
        JsonObject data;
        try { data = Json.ParseLenient(body)["data"] as JsonObject ?? throw new LaunchException("z.ai quota: no data in response"); }
        catch (System.Text.Json.JsonException) { throw new LaunchException("z.ai quota: the response is not JSON"); }
        var windows = new List<QuotaWindow>();
        foreach (var l in (data["limits"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var unit = Json.Num(l["unit"]);
            var number = Json.Num(l["number"]);
            var span = unit switch { 3 => $"{number}h", 5 => number == 1 ? "monthly" : $"{number}mo", 6 => number == 1 ? "weekly" : $"{number}w", _ => $"u{unit}x{number}" };
            var type = Json.Str(l, "type");
            var name = type == "TIME_LIMIT" ? $"mcp-{span}" : span;
            DateTimeOffset? reset = l["nextResetTime"] is JsonValue r && r.TryGetValue(out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : null;
            var detail = type == "TIME_LIMIT" ? $"{Json.Num(l["currentValue"])} of {Json.Num(l["usage"])} calls" : null;
            windows.Add(new QuotaWindow(name, Json.Dec(l, "percentage") ?? 0, reset, detail));
        }
        return new QuotaReading(provider, Json.Str(data, "level"), windows, DateTimeOffset.Now);
    }
}
