namespace LeanWorker;

/// <summary>
/// What a worker needs because of the model, whichever provider serves it (price book key "modelTraits").
/// </summary>
internal sealed record ModelTraits(string Key, List<string> AllowedTools, string? Note);