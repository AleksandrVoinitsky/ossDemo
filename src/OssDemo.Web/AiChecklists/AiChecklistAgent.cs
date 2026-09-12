using System.Globalization;
using System.Text.Json;

internal sealed class AiChecklistAgent(
    IAiChecklistFacilitySource facilitySource,
    IAiChecklistKnowledgeSearch knowledgeSearch,
    IAiChecklistSynthesisClient synthesisClient,
    IChecklistRepository checklists,
    ILogger<AiChecklistAgent> logger)
{
    public async Task<ChecklistOperationResult<AiChecklistAnalysis>> AnalyzeAsync(string? facilitySlug, CancellationToken cancellationToken)
    {
        var facility = await LoadFacilityAsync(facilitySlug, cancellationToken);
        return facility is null
            ? ChecklistOperationResult<AiChecklistAnalysis>.Fail("not_found", "Карточка объекта не найдена.")
            : ChecklistOperationResult<AiChecklistAnalysis>.Success(new(facility, AiChecklistQueryPlanner.Build(facility)));
    }

    public async Task<ChecklistOperationResult<AiChecklistSearchPreview>> SearchAsync(string? facilitySlug, CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(facilitySlug, cancellationToken);
        if (!analysis.IsSuccess)
            return ChecklistOperationResult<AiChecklistSearchPreview>.Fail(analysis.ErrorCode!, analysis.Error!, analysis.Errors);
        try
        {
            var evidence = await knowledgeSearch.SearchAsync(analysis.Value!.Queries, cancellationToken);
            return evidence.Count == 0
                ? ChecklistOperationResult<AiChecklistSearchPreview>.Fail("knowledge_empty", "По карточке объекта не найдено подходящих фрагментов в базе знаний.")
                : ChecklistOperationResult<AiChecklistSearchPreview>.Success(new(analysis.Value.Facility, analysis.Value.Queries, evidence));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Ошибка поиска оснований для ИИ-чек-листа объекта {FacilitySlug}.", facilitySlug);
            return ChecklistOperationResult<AiChecklistSearchPreview>.Fail("search_unavailable", "Система поиска временно недоступна.");
        }
    }

    public async Task<ChecklistOperationResult<ChecklistDetails>> GenerateAsync(string? facilitySlug, CancellationToken cancellationToken)
    {
        var search = await SearchAsync(facilitySlug, cancellationToken);
        if (!search.IsSuccess)
            return ChecklistOperationResult<ChecklistDetails>.Fail(search.ErrorCode!, search.Error!, search.Errors);

        try
        {
            var preview = search.Value!;
            var raw = await synthesisClient.SynthesizeAsync(preview.Facility, preview.Evidence, cancellationToken);
            var synthesis = AiChecklistOutputParser.Parse(raw, preview.Evidence);
            if (synthesis.Items.Count == 0)
                return ChecklistOperationResult<ChecklistDetails>.Fail("ai_invalid_response", "ИИ не сформировал пунктов с подтверждёнными источниками.");

            var facility = await facilitySource.GetFacilityAsync(preview.Facility.Slug, cancellationToken);
            if (facility is null)
                return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Объект проверки не найден.");

            var evidenceById = preview.Evidence.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var draftItems = synthesis.Items.Select(item =>
            {
                var sources = item.SourceIds.Select(id => evidenceById[id]).ToArray();
                var basis = string.Join("; ", item.Citations.Select(citation =>
                {
                    var source = evidenceById[citation.SourceId];
                    return $"{source.DocumentTitle} — {source.SourceLabel}: «{citation.Quote}»";
                }).Distinct(StringComparer.OrdinalIgnoreCase));
                var note = string.Join(" ", new[]
                {
                    item.Reason,
                    $"Уверенность ИИ: {item.Confidence.ToString("P0", CultureInfo.GetCultureInfo("ru-RU"))}. Источники: {string.Join(", ", item.SourceIds)}."
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                return new AiGeneratedDraftItem(item.Section, item.Title, basis, note);
            }).ToArray();

            return await checklists.CreateAiDraftAsync(new(facility.Id, facility.Name, synthesis.Name, draftItems), cancellationToken);
        }
        catch (AiChecklistGenerationException exception)
        {
            return ChecklistOperationResult<ChecklistDetails>.Fail(exception.Code, exception.Message);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "ИИ вернул некорректный JSON для объекта {FacilitySlug}.", facilitySlug);
            return ChecklistOperationResult<ChecklistDetails>.Fail("ai_invalid_response", "ИИ вернул некорректный формат чек-листа.");
        }
    }

    private async Task<FacilityProfile?> LoadFacilityAsync(string? slug, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(slug) ? null : await facilitySource.GetProfileAsync(slug.Trim(), cancellationToken);
}
