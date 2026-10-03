// Subscription quota: how much of a coding plan's usage windows is used. Read before a subscription-billed
// run (to pick a model with headroom) and after it (to record what the task used). Adapters per provider;
// z.ai's GLM Coding Plan is the one verified so far.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed record QuotaReading(string Provider, string? Level, List<QuotaWindow> Windows, DateTimeOffset ReadAt)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["provider"] = Provider,
            ["level"] = Level,
            ["read_at"] = ReadAt.ToString("o"),
            ["windows"] = new JsonArray([.. Windows.Select(w => (JsonNode)new JsonObject
            {
                ["name"] = w.Name,
                ["percent"] = w.Percent,
                ["resets_at"] = w.ResetsAt?.ToString("o"),
                ["detail"] = w.Detail,
            })]),
        };
    }

    public static QuotaReading FromJson(JsonObject o)
    {
        return new QuotaReading(
            Json.Str(o, "provider") ?? string.Empty, Json.Str(o, "level"),
            [.. (o["windows"] as JsonArray ?? []).OfType<JsonObject>().Select(w => new QuotaWindow(
                Json.Str(w, "name") ?? string.Empty, Json.Dec(w, "percent") ?? 0,
                Json.Str(w, "resets_at") is { } r ? DateTimeOffset.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : null,
                Json.Str(w, "detail")))],
            DateTimeOffset.Parse(Json.Str(o, "read_at") ?? DateTimeOffset.MinValue.ToString("o"), CultureInfo.InvariantCulture));
    }

    public string Line(QuotaReading? before = null) => string.Join(", ", Windows.Select(w =>
    {
        QuotaWindow? prev = before?.Windows.Find(b => b.Name == w.Name);
        string pct = prev is null || prev.Percent == w.Percent ? string.Create(CultureInfo.InvariantCulture, $"{w.Percent:0.#}%") : string.Create(CultureInfo.InvariantCulture, $"{prev.Percent:0.#}% -> {w.Percent:0.#}%");
        return $"{w.Name} {pct}{(w.ResetsAt is { } r ? $" (resets in {Until(r)})" : string.Empty)}";
    }));

    private static string Until(DateTimeOffset t)
    {
        TimeSpan d = t - DateTimeOffset.Now;
        if (d < TimeSpan.Zero)
        {
            return "0m";
        }

        if (d.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalDays}d{d.Hours}h");
        }

        if (d.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalHours}h{d.Minutes:00}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{d.Minutes}m");
    }
}