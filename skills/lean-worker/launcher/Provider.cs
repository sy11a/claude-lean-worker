using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class Provider(string name, JsonObject? o)
{
    public string Name { get; } = name;
    public JsonObject Raw { get; } = o ?? [];

    /// <summary>
    /// metered | subscription | auto (anthropic: subscription in lean mode without a key).
    /// </summary>
    public string Billing => Json.Str(Raw, "billing") ?? "metered";
    public string PriceAs => Json.Str(Raw, "priceAs") ?? Name;
    public string? AnthropicBaseUrl => Json.Str(Raw, "anthropicBaseUrl");
    public string? KeyEnv => Json.Str(Raw, "keyEnv");
    public JsonObject? Quota => Raw["quota"] as JsonObject;
}