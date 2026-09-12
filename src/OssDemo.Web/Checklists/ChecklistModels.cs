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
