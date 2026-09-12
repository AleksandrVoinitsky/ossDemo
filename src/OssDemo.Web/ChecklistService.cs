internal sealed class ChecklistService(IChecklistRepository repository)
{
    public Task<IReadOnlyList<ChecklistTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken) => repository.ListTemplatesAsync(cancellationToken);
    public Task<ChecklistTemplateDetails?> GetTemplateAsync(Guid id, CancellationToken cancellationToken) => repository.GetTemplateAsync(id, cancellationToken);
    public Task<ChecklistDetails?> GetChecklistAsync(Guid id, CancellationToken cancellationToken) => repository.GetChecklistAsync(id, cancellationToken);
    public Task<IReadOnlyList<ChecklistSummary>> ListDraftsAsync(CancellationToken cancellationToken) => repository.ListDraftsAsync(cancellationToken);
    public Task<IReadOnlyList<ChecklistSummary>> ListHistoryAsync(ChecklistHistoryFilter filter, CancellationToken cancellationToken) => repository.ListHistoryAsync(filter, cancellationToken);

    public async Task<ChecklistOperationResult<ChecklistTemplateDetails>> CreateTemplateAsync(ChecklistTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        var normalized = ChecklistRules.NormalizeTemplate(request);
        return normalized.IsSuccess
            ? ChecklistOperationResult<ChecklistTemplateDetails>.Success(await repository.CreateTemplateAsync(normalized.Value!, cancellationToken))
            : ChecklistOperationResult<ChecklistTemplateDetails>.Fail(normalized.ErrorCode!, normalized.Error!, normalized.Errors);
    }

    public async Task<ChecklistOperationResult<ChecklistTemplateDetails>> UpdateTemplateAsync(Guid id, ChecklistTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        var normalized = ChecklistRules.NormalizeTemplate(request);
        return normalized.IsSuccess
            ? await repository.UpdateTemplateAsync(id, normalized.Value!, cancellationToken)
            : ChecklistOperationResult<ChecklistTemplateDetails>.Fail(normalized.ErrorCode!, normalized.Error!, normalized.Errors);
    }

    public async Task<ChecklistOperationResult<ChecklistTemplateDetails>> CopyTemplateAsync(Guid id, string? name, CancellationToken cancellationToken)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        return normalizedName.Length == 0
            ? ChecklistOperationResult<ChecklistTemplateDetails>.Fail("validation", "Укажите название копии.", new Dictionary<string, string[]> { ["name"] = ["Укажите название копии."] })
            : await repository.CopyTemplateAsync(id, normalizedName, cancellationToken);
    }

    public async Task<ChecklistOperationResult<bool>> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        return await repository.DeleteTemplateAsync(id, cancellationToken)
            ? ChecklistOperationResult<bool>.Success(true)
            : ChecklistOperationResult<bool>.Fail("not_found", "Шаблон не найден.");
    }

    public Task<ChecklistOperationResult<ChecklistDetails>> CreateDraftAsync(CreateChecklistRequest request, CancellationToken cancellationToken)
    {
        if (request.TemplateId == Guid.Empty || request.FacilityId == Guid.Empty)
            return Task.FromResult(ChecklistOperationResult<ChecklistDetails>.Fail("validation", "Выберите шаблон и объект проверки."));
        if (request.InspectionStartedOn is not null && request.InspectionFinishedOn < request.InspectionStartedOn)
            return Task.FromResult(ChecklistOperationResult<ChecklistDetails>.Fail("validation", "Дата окончания проверки не может быть раньше даты начала."));
        return repository.CreateDraftAsync(request with { Name = request.Name?.Trim() }, cancellationToken);
    }

    public Task<ChecklistOperationResult<ChecklistDetails>> AddItemAsync(Guid id, AddChecklistItemRequest request, CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var basis = request.Basis?.Trim() ?? string.Empty;
        if (title.Length == 0 || basis.Length == 0)
            return Task.FromResult(ChecklistOperationResult<ChecklistDetails>.Fail("validation", "Укажите наименование и основание пункта."));
        return repository.AddDraftItemAsync(id, request with { Title = title, Basis = basis, Section = request.Section?.Trim(), Note = request.Note?.Trim() }, cancellationToken);
    }

    public Task<ChecklistOperationResult<ChecklistDetails>> ApproveAsync(Guid id, string approvedBy, CancellationToken cancellationToken) =>
        repository.ApproveAsync(id, approvedBy, cancellationToken);
}
