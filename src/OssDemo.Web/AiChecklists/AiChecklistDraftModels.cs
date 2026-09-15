internal static class InspectionBasisDocumentType
{
    public const string Order = "order";
    public const string Directive = "directive";
    public const string License = "license";

    public static bool TryParse(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Order or Directive or License;
    }
}

internal sealed record InspectionBasisDocument(
    Guid Id,
    Guid DraftId,
    string Type,
    string OriginalName,
    string MediaType,
    long ByteLength,
    string Sha256,
    DateTimeOffset UploadedAt);

internal sealed record AiChecklistDraftState(
    Guid Id,
    string FacilitySlug,
    Guid? TemplateId,
    string Step,
    long Version,
    IReadOnlyList<InspectionBasisDocument> Documents,
    Guid? RunId = null,
    DateTimeOffset? UpdatedAt = null);

internal sealed record CreateAiChecklistWorkflowDraftRequest(string? FacilitySlug, Guid? TemplateId);
internal sealed record UpdateAiChecklistDraftRequest(string? FacilitySlug, Guid? TemplateId, string? Step, long Version);
internal sealed record CreateAiChecklistRunRequest(Guid DraftId, string? FacilitySlug = null);

internal static class AiChecklistDraftRules
{
    private const long MaximumFileSize = 30L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".rtf", ".txt"];
    private static readonly HashSet<string> AllowedSteps = ["object", "basis", "template", "profile", "criteria", "generation"];

    public static bool CanContinue(IReadOnlyList<InspectionBasisDocument> documents) => true;
    public static bool IsAllowedFile(string? fileName, long length) =>
        length is > 0 and <= MaximumFileSize && AllowedExtensions.Contains(Path.GetExtension(fileName ?? string.Empty));
    public static string NormalizeStep(string? step) => AllowedSteps.Contains(step?.Trim().ToLowerInvariant() ?? string.Empty)
        ? step!.Trim().ToLowerInvariant()
        : "object";
}
