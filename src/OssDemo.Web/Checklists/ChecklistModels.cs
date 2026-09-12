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
