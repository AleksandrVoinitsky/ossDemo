internal sealed record ChecklistTemplateWriteRequest(
    string? Name,
    Guid FacilityId,
    long Version,
    IReadOnlyList<ChecklistTemplateSectionWrite> Sections);

internal sealed record ChecklistTemplateSectionWrite(
    string? Title,
    int Position,
    IReadOnlyList<ChecklistTemplateItemWrite> Items);

internal sealed record ChecklistTemplateItemWrite(
    string? Title,
    string? Basis,
    string? Note,
    int Position);

internal enum ChecklistStatus
{
    Draft,
    Approved
}

internal sealed record ChecklistTemplateSummary(Guid Id, string Name, Guid FacilityId, string Facility, long Version, int SectionCount, int ItemCount, DateTimeOffset UpdatedAt);
internal sealed record ChecklistTemplateDetails(Guid Id, string Name, Guid FacilityId, string Facility, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChecklistTemplateSectionDetails> Sections);
internal sealed record ChecklistTemplateSectionDetails(Guid Id, string Title, int Position, IReadOnlyList<ChecklistTemplateItemDetails> Items);
internal sealed record ChecklistTemplateItemDetails(Guid Id, string Title, string Basis, string Note, int Position);
internal sealed record CopyChecklistTemplateRequest(string? Name);
internal sealed record CreateChecklistRequest(Guid TemplateId, string? Name, Guid FacilityId, DateOnly? InspectionStartedOn, DateOnly? InspectionFinishedOn);
internal sealed record AddChecklistItemRequest(string? Title, string? Basis, string? Section, string? Note);
internal sealed record UpdateChecklistItemRequest(string? Result, string? Nonconformity, string? Note);
internal sealed record ChecklistHistoryFilter(string? Search, Guid? FacilityId, DateOnly? From, DateOnly? To);
internal sealed record ChecklistSummary(Guid Id, string Name, Guid? FacilityId, string Facility, string TemplateName, string Status, DateOnly? InspectionStartedOn, DateOnly? InspectionFinishedOn, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ApprovedAt, string? ApprovedBy, int ItemCount);
internal sealed record ChecklistDetails(Guid Id, string Name, Guid? FacilityId, string Facility, Guid? TemplateId, string TemplateName, string Status, DateOnly? InspectionStartedOn, DateOnly? InspectionFinishedOn, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ApprovedAt, string? ApprovedBy, IReadOnlyList<ChecklistItemDetails> Items);
internal sealed record ChecklistItemDetails(Guid Id, int Position, string Section, string Title, string Basis, string Result, string Nonconformity, string Note, string Origin, string SourceLabel = "");

internal sealed record ChecklistSeedTemplate(Guid Id, string SourceKey, string Name, string Facility, IReadOnlyList<ChecklistSeedSection> Sections);
internal sealed record ChecklistSeedSection(string Title, IReadOnlyList<ChecklistSeedTemplateItem> Items);
internal sealed record ChecklistSeedTemplateItem(string Title, string Basis, string Note);
internal sealed record ChecklistSeedHistory(Guid Id, string SourceKey, string Name, string Facility, DateOnly? StartedOn, DateOnly? FinishedOn, DateTimeOffset ApprovedAt, ChecklistStatus Status, IReadOnlyList<ChecklistSeedHistoryItem> Items);
internal sealed record ChecklistSeedHistoryItem(string Section, string Title, string Basis, string Result, string Nonconformity, string Note);

internal sealed record ChecklistOperationResult<T>(
    bool IsSuccess,
    T? Value,
    string? ErrorCode,
    string? Error,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public static ChecklistOperationResult<T> Success(T value) =>
        new(true, value, null, null, new Dictionary<string, string[]>());

    public static ChecklistOperationResult<T> Fail(
        string code,
        string error,
        IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(false, default, code, error, errors ?? new Dictionary<string, string[]>());
}
