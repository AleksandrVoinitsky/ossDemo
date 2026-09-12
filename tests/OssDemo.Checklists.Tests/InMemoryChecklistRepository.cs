internal sealed class InMemoryChecklistRepository : IChecklistRepository
{
    private readonly Dictionary<Guid, ChecklistTemplateDetails> templates = [];
    private readonly Dictionary<Guid, ChecklistDetails> checklists = [];

    public Task<IReadOnlyList<ChecklistTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChecklistTemplateSummary>>(templates.Values.Select(ToSummary).ToArray());

    public Task<ChecklistTemplateDetails?> GetTemplateAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(templates.GetValueOrDefault(id) is { } value ? Clone(value) : null);

    public Task<ChecklistTemplateDetails> CreateTemplateAsync(ChecklistTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var value = new ChecklistTemplateDetails(Guid.NewGuid(), request.Name!, request.FacilityId, "Тестовый объект", 1, now, now, MapSections(request.Sections));
        templates[value.Id] = value;
        return Task.FromResult(Clone(value));
    }

    public Task<ChecklistOperationResult<ChecklistTemplateDetails>> UpdateTemplateAsync(Guid id, ChecklistTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        if (!templates.TryGetValue(id, out var current)) return Task.FromResult(FailTemplate("not_found", "Шаблон не найден."));
        if (current.Version != request.Version) return Task.FromResult(FailTemplate("version_conflict", "Шаблон уже изменён."));
        var value = current with { Name = request.Name!, FacilityId = request.FacilityId, Version = current.Version + 1, UpdatedAt = DateTimeOffset.UtcNow, Sections = MapSections(request.Sections) };
        templates[id] = value;
        return Task.FromResult(ChecklistOperationResult<ChecklistTemplateDetails>.Success(Clone(value)));
    }

    public async Task<ChecklistOperationResult<ChecklistTemplateDetails>> CopyTemplateAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        if (!templates.TryGetValue(id, out var source)) return FailTemplate("not_found", "Шаблон не найден.");
        var request = new ChecklistTemplateWriteRequest(name, source.FacilityId, 0, source.Sections.Select(section =>
            new ChecklistTemplateSectionWrite(section.Title, section.Position, section.Items.Select(item => new ChecklistTemplateItemWrite(item.Title, item.Basis, item.Note, item.Position)).ToArray())).ToArray());
        return ChecklistOperationResult<ChecklistTemplateDetails>.Success(await CreateTemplateAsync(request, cancellationToken));
    }

    public Task<bool> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(templates.Remove(id));

    public Task<ChecklistOperationResult<ChecklistDetails>> CreateDraftAsync(CreateChecklistRequest request, CancellationToken cancellationToken)
    {
        if (!templates.TryGetValue(request.TemplateId, out var template)) return Task.FromResult(FailChecklist("not_found", "Шаблон не найден."));
        var now = DateTimeOffset.UtcNow;
        var items = template.Sections.SelectMany(section => section.Items.Select(item => new { section.Title, Item = item })).Select((value, index) =>
            new ChecklistItemDetails(Guid.NewGuid(), index + 1, value.Title, value.Item.Title, value.Item.Basis, "", "", value.Item.Note, "template")).ToArray();
        var checklist = new ChecklistDetails(Guid.NewGuid(), string.IsNullOrWhiteSpace(request.Name) ? template.Name : request.Name!, request.FacilityId, "Тестовый объект", template.Id, template.Name, "draft", request.InspectionStartedOn, request.InspectionFinishedOn, now, now, null, null, items);
        checklists[checklist.Id] = checklist;
        return Task.FromResult(ChecklistOperationResult<ChecklistDetails>.Success(Clone(checklist)));
    }

    public Task<ChecklistDetails?> GetChecklistAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(checklists.GetValueOrDefault(id) is { } value ? Clone(value) : null);

    public Task<IReadOnlyList<ChecklistSummary>> ListDraftsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChecklistSummary>>(checklists.Values.Where(item => item.Status == "draft").Select(ToSummary).ToArray());

    public Task<IReadOnlyList<ChecklistSummary>> ListHistoryAsync(ChecklistHistoryFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChecklistSummary>>(checklists.Values.Where(item => item.Status == "approved").Select(ToSummary).ToArray());

    public Task<ChecklistOperationResult<ChecklistDetails>> AddDraftItemAsync(Guid id, AddChecklistItemRequest request, CancellationToken cancellationToken)
    {
        if (!checklists.TryGetValue(id, out var checklist)) return Task.FromResult(FailChecklist("not_found", "Чек-лист не найден."));
        if (checklist.Status != "draft") return Task.FromResult(FailChecklist("state_conflict", "Утверждённый чек-лист нельзя изменять."));
        var items = checklist.Items.Append(new ChecklistItemDetails(Guid.NewGuid(), checklist.Items.Count + 1, request.Section ?? "Ручной пункт", request.Title!, request.Basis!, "", "", request.Note ?? "", "manual")).ToArray();
        checklist = checklist with { Items = items, UpdatedAt = DateTimeOffset.UtcNow };
        checklists[id] = checklist;
        return Task.FromResult(ChecklistOperationResult<ChecklistDetails>.Success(Clone(checklist)));
    }

    public Task<ChecklistOperationResult<ChecklistDetails>> ApproveAsync(Guid id, string approvedBy, CancellationToken cancellationToken)
    {
        if (!checklists.TryGetValue(id, out var checklist)) return Task.FromResult(FailChecklist("not_found", "Чек-лист не найден."));
        if (checklist.Status != "draft") return Task.FromResult(FailChecklist("state_conflict", "Чек-лист уже утверждён."));
        var now = DateTimeOffset.UtcNow;
        checklist = checklist with { Status = "approved", ApprovedAt = now, ApprovedBy = approvedBy, UpdatedAt = now };
        checklists[id] = checklist;
        return Task.FromResult(ChecklistOperationResult<ChecklistDetails>.Success(Clone(checklist)));
    }

    private static IReadOnlyList<ChecklistTemplateSectionDetails> MapSections(IReadOnlyList<ChecklistTemplateSectionWrite> sections) =>
        sections.Select(section => new ChecklistTemplateSectionDetails(Guid.NewGuid(), section.Title!, section.Position,
            section.Items.Select(item => new ChecklistTemplateItemDetails(Guid.NewGuid(), item.Title!, item.Basis!, item.Note ?? "", item.Position)).ToArray())).ToArray();
    private static ChecklistTemplateSummary ToSummary(ChecklistTemplateDetails value) => new(value.Id, value.Name, value.FacilityId, value.Facility, value.Version, value.Sections.Count, value.Sections.Sum(x => x.Items.Count), value.UpdatedAt);
    private static ChecklistSummary ToSummary(ChecklistDetails value) => new(value.Id, value.Name, value.FacilityId, value.Facility, value.TemplateName, value.Status, value.InspectionStartedOn, value.InspectionFinishedOn, value.CreatedAt, value.UpdatedAt, value.ApprovedAt, value.ApprovedBy, value.Items.Count);
    private static ChecklistTemplateDetails Clone(ChecklistTemplateDetails value) => value with { Sections = value.Sections.Select(section => section with { Items = section.Items.ToArray() }).ToArray() };
    private static ChecklistDetails Clone(ChecklistDetails value) => value with { Items = value.Items.ToArray() };
    private static ChecklistOperationResult<ChecklistTemplateDetails> FailTemplate(string code, string error) => ChecklistOperationResult<ChecklistTemplateDetails>.Fail(code, error);
    private static ChecklistOperationResult<ChecklistDetails> FailChecklist(string code, string error) => ChecklistOperationResult<ChecklistDetails>.Fail(code, error);
}
