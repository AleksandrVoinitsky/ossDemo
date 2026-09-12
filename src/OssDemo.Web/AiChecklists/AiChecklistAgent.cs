using System.Globalization;
using System.Text.Json;

internal sealed class AiChecklistAgent(
    IAiChecklistFacilitySource facilitySource,
    IAiChecklistKnowledgeSearch knowledgeSearch,
    IAiChecklistSynthesisClient synthesisClient,
    IChecklistRepository checklists,
    IAiChecklistRunStore runStore,
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
            var preparedEvidence = AiChecklistBatchPlanner.PrepareEvidence(preview.Evidence);
            var firstBatch = AiChecklistBatchPlanner.Build(preparedEvidence).First();
            var selectedEvidence = preparedEvidence.Where(item => firstBatch.EvidenceIds.Contains(item.Id)).ToArray();
            var raw = await synthesisClient.SynthesizeAsync(preview.Facility, selectedEvidence, cancellationToken);
            var synthesis = AiChecklistOutputParser.Parse(raw, selectedEvidence);
            if (synthesis.Items.Count == 0)
                return ChecklistOperationResult<ChecklistDetails>.Fail("ai_invalid_response", "ИИ не сформировал пунктов с подтверждёнными источниками.");

            var facility = await facilitySource.GetFacilityAsync(preview.Facility.Slug, cancellationToken);
            if (facility is null)
                return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Объект проверки не найден.");

            var draftItems = ToDraftItems(synthesis.Items, selectedEvidence);

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

    public async Task<ChecklistOperationResult<AiChecklistRunState>> CreateRunAsync(string? facilitySlug, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var search = await SearchAsync(facilitySlug, cancellationToken);
        if (!search.IsSuccess) return ChecklistOperationResult<AiChecklistRunState>.Fail(search.ErrorCode!, search.Error!, search.Errors);
        var preview = search.Value!;
        var facility = await facilitySource.GetFacilityAsync(preview.Facility.Slug, cancellationToken);
        if (facility is null) return ChecklistOperationResult<AiChecklistRunState>.Fail("not_found", "Объект проверки не найден.");
        var evidence = AiChecklistBatchPlanner.PrepareEvidence(preview.Evidence);
        var batches = AiChecklistBatchPlanner.Build(evidence);
        var run = await runStore.CreateAsync(preview.Facility, facility.Id, facility.Name, evidence, batches, cancellationToken);
        logger.LogInformation("Создан запуск ИИ-чек-листа {RunId}: {EvidenceCount} фрагментов источников, {BatchCount} пакетов, поиск {DurationMs} мс.", run.Id, evidence.Count, batches.Count, started.ElapsedMilliseconds);
        return ChecklistOperationResult<AiChecklistRunState>.Success(run);
    }

    public Task<AiChecklistRunState?> GetRunAsync(Guid runId, CancellationToken cancellationToken) => runStore.GetAsync(runId, cancellationToken);
    public Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken) => runStore.QueueBatchAsync(runId, batchIndex, cancellationToken);

    public async Task<bool> ProcessNextBatchAsync(CancellationToken cancellationToken)
    {
        var work = await runStore.ClaimNextBatchAsync(cancellationToken);
        if (work is null) return false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var contextCharacters = work.Evidence.Sum(item => item.Text.Length);
        logger.LogInformation("Запуск {RunId}, пакет {BatchIndex}: отправка {EvidenceCount} источников, {ContextCharacters} символов.", work.RunId, work.Batch.Index, work.Evidence.Count, contextCharacters);
        try
        {
            var raw = await synthesisClient.SynthesizeAsync(work.Facility, work.Evidence, cancellationToken);
            var parsed = AiChecklistOutputParser.Parse(raw, work.Evidence);
            var items = parsed.Items.Take(20).ToArray();
            await PersistOutcomeAsync(
                () => runStore.CompleteBatchAsync(work.RunId, work.Batch.Index, items, timer.ElapsedMilliseconds, cancellationToken),
                work.RunId,
                work.Batch.Index,
                cancellationToken);
            logger.LogInformation("Запуск {RunId}, пакет {BatchIndex}: принято {ItemCount} пунктов за {DurationMs} мс.", work.RunId, work.Batch.Index, items.Length, timer.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = exception is AiChecklistGenerationException generation ? generation.Message : "Не удалось обработать пакет.";
            await PersistOutcomeAsync(
                () => runStore.FailBatchAsync(work.RunId, work.Batch.Index, message, timer.ElapsedMilliseconds, cancellationToken),
                work.RunId,
                work.Batch.Index,
                cancellationToken);
            logger.LogError(exception, "Запуск {RunId}, пакет {BatchIndex} завершился ошибкой за {DurationMs} мс.", work.RunId, work.Batch.Index, timer.ElapsedMilliseconds);
        }
        return true;
    }

    public async Task<ChecklistOperationResult<ChecklistDetails>> FinalizeRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await runStore.GetAsync(runId, cancellationToken);
        if (run is null) return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Запуск ИИ-формирования не найден.");
        if (run.ChecklistId is { } checklistId)
        {
            var existing = await checklists.GetChecklistAsync(checklistId, cancellationToken);
            return existing is null ? ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Созданный чек-лист не найден.") : ChecklistOperationResult<ChecklistDetails>.Success(existing);
        }
        if (run.Batches.Any(item => item.Status != "completed")) return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Дождитесь завершения всех тематических пакетов.");
        if (!await runStore.BeginFinalizeAsync(runId, cancellationToken)) return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Финализация уже выполняется.");
        try
        {
            var items = run.Batches.SelectMany(batch => batch.Items).DistinctBy(item => item.Title.Trim(), StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            if (items.Length == 0) throw new AiChecklistGenerationException("ai_invalid_response", "ИИ не сформировал подтверждённых пунктов.");
            var result = await checklists.CreateAiDraftAsync(new(run.FacilityId, run.FacilityName, $"ИИ-чек-лист — {run.FacilityName}", ToDraftItems(items, run.Evidence), runId), cancellationToken);
            if (!result.IsSuccess) { await runStore.CancelFinalizeAsync(runId, result.Error ?? "Ошибка сохранения.", cancellationToken); return result; }
            await runStore.CompleteFinalizeAsync(runId, result.Value!.Id, cancellationToken);
            return result;
        }
        catch (AiChecklistGenerationException exception)
        {
            await runStore.CancelFinalizeAsync(runId, exception.Message, cancellationToken);
            return ChecklistOperationResult<ChecklistDetails>.Fail(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось финализировать запуск ИИ-чек-листа {RunId}.", runId);
            await runStore.CancelFinalizeAsync(runId, "Ошибка сохранения черновика.", cancellationToken);
            return ChecklistOperationResult<ChecklistDetails>.Fail("storage_unavailable", "Не удалось сохранить черновик. Повторите финализацию.");
        }
    }

    private static IReadOnlyList<AiGeneratedDraftItem> ToDraftItems(IReadOnlyList<AiGeneratedChecklistItem> items, IReadOnlyList<AiChecklistEvidence> evidence)
    {
        var evidenceById = evidence.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        return items.Select(item =>
        {
            var basis = string.Join("; ", item.Citations.Select(citation => { var source = evidenceById[citation.SourceId]; return $"{source.DocumentTitle} — {source.SourceLabel}: «{citation.Quote}»"; }).Distinct(StringComparer.OrdinalIgnoreCase));
            var note = string.Join(" ", new[] { item.Reason, $"Уверенность ИИ: {item.Confidence.ToString("P0", CultureInfo.GetCultureInfo("ru-RU"))}. Источники: {string.Join(", ", item.SourceIds)}." }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new AiGeneratedDraftItem(item.Section, item.Title, basis, note);
        }).ToArray();
    }

    private async Task PersistOutcomeAsync(Func<Task> persist, Guid runId, int batchIndex, CancellationToken cancellationToken)
    {
        while (true)
        {
            try { await persist(); return; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Запуск {RunId}, пакет {BatchIndex}: БД недоступна, сохранение результата будет повторено.", runId, batchIndex);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task<FacilityProfile?> LoadFacilityAsync(string? slug, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(slug) ? null : await facilitySource.GetProfileAsync(slug.Trim(), cancellationToken);
}
