internal sealed record AiChecklistSearchQuery(string Key, string Label, string Query);

internal sealed record AiChecklistEvidence(
    string Id,
    string QueryLabel,
    string DocumentTitle,
    string SourceLabel,
    string Text,
    double Score);

internal sealed record AiGeneratedChecklistItem(
    string Section,
    string Title,
    string Reason,
    double Confidence,
    IReadOnlyList<string> SourceIds);

internal sealed record AiChecklistSynthesis(
    string Name,
    IReadOnlyList<AiGeneratedChecklistItem> Items);

internal sealed class AiChecklistGenerationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
