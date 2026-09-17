using System.Globalization;
using System.Text.Json;

internal sealed class AiChecklistAgent(
    IAiChecklistFacilitySource facilitySource,
    IAiChecklistKnowledgeSearch knowledgeSearch,
    IAiChecklistSynthesisClient synthesisClient,
    IChecklistRepository checklists,
    IAiChecklistRunStore runStore,
    ILogger<AiChecklistAgent> logger,
    IClassifierRepository? classifierRepository = null,
    IAiChecklistHistoryReferenceSource? historyReferenceSource = null,
    IInspectorChecklistTemplateSource? inspectorTemplateSource = null,
    FacilityChecklistCatalogs? checklistCatalogs = null,
    IRequirementWorkspace? requirementWorkspace = null)
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

    public async Task<ChecklistOperationResult<AiChecklistRunState>> CreateRunAsync(string? facilitySlug, CancellationToken cancellationToken, Guid? draftId = null, Guid? templateId = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var analysis = await AnalyzeAsync(facilitySlug, cancellationToken);
        if (!analysis.IsSuccess) return ChecklistOperationResult<AiChecklistRunState>.Fail(analysis.ErrorCode!, analysis.Error!, analysis.Errors);
        var facility = await facilitySource.GetFacilityAsync(analysis.Value!.Facility.Slug, cancellationToken);
        if (facility is null) return ChecklistOperationResult<AiChecklistRunState>.Fail("not_found", "Объект проверки не найден.");
        var tree = classifierRepository is null ? ClassifierSeedData.Tree : await classifierRepository.GetTreeAsync(cancellationToken);
        var facts = FacilityFactNormalizer.Normalize(analysis.Value.Facility.Profile, null);
        var history = historyReferenceSource is null ? [] : await historyReferenceSource.GetAsync(facility.Name,tree,cancellationToken);
        var decisions = ClassifierApplicabilityMatcher.Decide(tree, facts, history);
        if (checklistCatalogs is not null)
        {
            IReadOnlySet<string>? allowedRequirementIds = null;
            if (requirementWorkspace is not null)
            {
                var resolved = await Task.WhenAll(decisions
                    .Where(item => item.Outcome == "included")
                    .Select(item => requirementWorkspace.ResolveCriterionAsync(item.Criterion.Id, cancellationToken)));
                allowedRequirementIds = resolved.Where(item => item is not null)
                    .SelectMany(item => item!.Items)
                    .Select(item => item.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            var composition = new FacilityChecklistComposer(tree, checklistCatalogs.Requirements)
                .Compose(analysis.Value.Facility.Profile, history, allowedRequirementIds: allowedRequirementIds);
            var structured = analysis.Value.Facility.Profile.StructuredProfile
                ?? FacilityProfileMigration.FromLegacy(analysis.Value.Facility.Slug, analysis.Value.Facility.Profile);
            if (composition.Gaps.Count > 0)
                logger.LogWarning("Для объекта {FacilitySlug} у {GapCount} применимых требований пока нет подтверждённой проверочной процедуры; формирование продолжается по найденному покрытию.",
                    facilitySlug, composition.Gaps.Count);

            var snapshot = new AiChecklistRunSnapshot(
                structured.SchemaVersion,
                structured.VerificationStatus,
                composition.ClassifierDecisions.Select(decision => new AiChecklistClassifierDecisionSnapshot(
                    decision.Code, decision.Outcome, decision.Reason,
                    decision.Facts.Select(fact => new AiChecklistDecisionFactSnapshot(fact.Code, fact.State.ToString().ToLowerInvariant(), fact.Details)).ToArray())).ToArray(),
                composition.SelectedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                composition.Items.Select(item => item.Id).ToArray(),
                composition.Gaps.Select(gap => new AiChecklistCoverageGapSnapshot(gap.RequirementId, gap.Reason)).ToArray(),
                checklistCatalogs.CatalogHashes,
                composition.Items.Select(item => new AiChecklistItemTraceSnapshot(item.Id, item.Title, item.Basis,
                    item.ClassifierCodes, item.RequirementIds, item.Provenance,
                    $"{item.LinkExplanation} Статус нормативной связи: {item.LinkStatus}.")).ToArray(),
                templateId);

            var catalogEvidence = composition.Items.Select(item => new AiChecklistEvidence(
                $"CAT-{item.Id}", item.Section, "Утверждённая контрольная процедура", item.Basis, item.Title, 1)).ToArray();
            var catalogPlans = composition.Items.GroupBy(item => item.Section)
                .SelectMany(section => section.Select((item, index) => (Item: item, Index: index))
                    .GroupBy(pair => pair.Index / 50)
                    .Select(batch => new
                    {
                        Section = section.Key,
                        Items = batch.Select(pair => pair.Item).ToArray()
                    }))
                .Select((batch, index) => new AiChecklistBatchPlan(
                    index, batch.Section, batch.Items.Select(item => $"CAT-{item.Id}").ToArray(),
                    batch.Items.Sum(item => item.Title.Length + item.Basis.Length),
                    batch.Items.SelectMany(item => item.ClassifierCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    "Пункты выбраны по подтвержденным фактам карточки, классификатору и утвержденному реестру требований.",
                    null, null, batch.Section,
                    batch.Items.SelectMany(item => item.RequirementIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
            var catalogRun = await runStore.CreateAsync(analysis.Value.Facility, facility.Id, facility.Name, catalogEvidence, catalogPlans, cancellationToken, snapshot, draftId);
            logger.LogInformation("Создан детерминированный запуск {RunId}: {RequirementCount} требований, {ItemCount} пунктов, LLM не требуется.", catalogRun.Id, composition.SelectedRequirements.Count, composition.Items.Count);
            return ChecklistOperationResult<AiChecklistRunState>.Success(catalogRun);
        }
        var applicable = ClassifierApplicabilityMatcher.Match(decisions, facts);
        var blockedCount = decisions.Count(item => item.Outcome == "blocked_unknown");
        if (blockedCount > 0)
            logger.LogWarning("Карточка {FacilitySlug}: {BlockedCount} решений классификатора заблокированы неизвестными фактами.", facilitySlug, blockedCount);
        var templateSections = inspectorTemplateSource?.Items.Select(item=>item.SectionCode).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var plans = new List<AiChecklistBatchPlan>();
        foreach(var sectionMatches in applicable.GroupBy(match=>match.Section.Code).OrderBy(group=>group.First().Section.Position))
        {
            if(templateSections.Contains(sectionMatches.Key))
            {
                var first=sectionMatches.First();
                plans.Add(new(plans.Count,first.Section.Title,[],0,sectionMatches.Select(match=>match.Criterion.Code).ToArray(),
                    string.Join(" ",sectionMatches.Select(match=>match.Reason).Distinct(StringComparer.OrdinalIgnoreCase)),null,null,first.Section.Title));
                continue;
            }
            foreach(var match in sectionMatches)
            {
                var query=AiChecklistQueryPlanner.Build(match,facts);
                plans.Add(new(plans.Count,$"{match.Criterion.Code} · {match.Section.Title}",[],0,[match.Criterion.Code],match.Reason,query.Query,match.Criterion.CheckText,match.Section.Title));
            }
        }
        var batches = plans.ToArray();
        var run = await runStore.CreateAsync(analysis.Value.Facility, facility.Id, facility.Name, [], batches, cancellationToken, draftId: draftId);
        logger.LogInformation("Создан запуск ИИ-чек-листа {RunId}: {CriterionCount} критериев классификатора за {DurationMs} мс.", run.Id, batches.Length, started.ElapsedMilliseconds);
        return ChecklistOperationResult<AiChecklistRunState>.Success(run);
    }

    public Task<AiChecklistRunState?> GetRunAsync(Guid runId, CancellationToken cancellationToken) => runStore.GetAsync(runId, cancellationToken);

    public async Task<AiChecklistTracePage?> GetTracePageAsync(Guid runId, int offset, int limit, CancellationToken cancellationToken)
    {
        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 100);
        return await runStore.GetTracePageAsync(runId, normalizedOffset, normalizedLimit, cancellationToken);
    }

    public async Task<AiChecklistRequirementPage?> GetRequirementPageAsync(Guid runId, int offset, int limit, CancellationToken cancellationToken)
    {
        var run = await runStore.GetAsync(runId, cancellationToken);
        if (run?.Snapshot is null || checklistCatalogs is null) return null;
        var items = run.Snapshot.SelectedRequirementIds
            .Select(checklistCatalogs.Requirements.Find)
            .Where(item => item is not null)
            .Cast<RequirementCatalogItem>()
            .ToArray();
        var normalizedOffset = Math.Clamp(offset, 0, items.Length);
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        return new(items.Skip(normalizedOffset).Take(normalizedLimit).Select(item => new AiChecklistRequirementSnapshot(
            item.Id, item.ClassifierCodes, item.Levels, item.Basis, item.Requirement)).ToArray(), items.Length, normalizedOffset, normalizedLimit);
    }

    public Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken) => runStore.QueueBatchAsync(runId, batchIndex, cancellationToken);
    public Task<bool> QueueBatchesAsync(Guid runId, IReadOnlyList<int> batchIndexes, CancellationToken cancellationToken) => runStore.QueueBatchesAsync(runId, batchIndexes, cancellationToken);
    public Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken) => runStore.StopAsync(runId, cancellationToken);

    public async Task<bool> ProcessNextBatchAsync(CancellationToken cancellationToken)
    {
        var work = await runStore.ClaimNextBatchAsync(cancellationToken);
        if (work is null) return false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("Запуск {RunId}, критерий {BatchIndex}: начат поиск оснований.", work.RunId, work.Batch.Index);
        try
        {
            var catalogEvidence = work.Evidence.Where(item => item.Id.StartsWith("CAT-", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (catalogEvidence.Length > 0)
            {
                var catalogItems = catalogEvidence.Select(item => new AiGeneratedChecklistItem(
                    item.QueryLabel, item.Text, work.Batch.ApplicabilityReason ?? "Требование применимо к подтвержденной карточке объекта.", 1,
                    [new(item.Id, item.Text)])).ToArray();
                await runStore.UpdateProgressAsync(work.RunId, work.Batch.Index, "validating", "Пункт выбран из утвержденного реестра требований", catalogItems.Length, string.Empty, cancellationToken);
                await PersistOutcomeAsync(() => runStore.CompleteCriterionAsync(work.RunId, work.Batch.Index, catalogEvidence, catalogItems, timer.ElapsedMilliseconds, cancellationToken), work.RunId, work.Batch.Index, cancellationToken);
                return true;
            }
            var sectionCode=work.Batch.CriterionCodes?.FirstOrDefault()?.Split('.')[0];
            var templateItems=string.IsNullOrWhiteSpace(sectionCode) || inspectorTemplateSource is null
                ? []
                : InspectorChecklistTemplate.SelectSections(inspectorTemplateSource.Items,[sectionCode]);
            if(templateItems.Count>0)
            {
                var templateEvidence=templateItems.Select(item=>new AiChecklistEvidence($"TPL-{item.Id}",item.Section,
                    "Рабочий шаблон инспекционного контроля",item.Basis,item.Title,1)).ToArray();
                var templateResults=templateItems.Select(item=>new AiGeneratedChecklistItem(item.Section,item.Title,
                    work.Batch.ApplicabilityReason ?? "Раздел применим к карточке объекта.",1,
                    [new($"TPL-{item.Id}",item.Title)])).ToArray();
                await runStore.UpdateProgressAsync(work.RunId,work.Batch.Index,"validating",
                    $"Из утверждённого рабочего слоя выбрано пунктов: {templateResults.Length}",templateResults.Length,string.Empty,cancellationToken);
                await PersistOutcomeAsync(
                    ()=>runStore.CompleteCriterionAsync(work.RunId,work.Batch.Index,templateEvidence,templateResults,timer.ElapsedMilliseconds,cancellationToken),
                    work.RunId,work.Batch.Index,cancellationToken);
                logger.LogInformation("Запуск {RunId}, раздел {SectionCode}: без LLM принято {ItemCount} утверждённых пунктов.",work.RunId,sectionCode,templateResults.Length);
                return true;
            }
            var evidence = work.Evidence;
            if (!string.IsNullOrWhiteSpace(work.Batch.Query))
            {
                var found = await knowledgeSearch.SearchAsync([new(work.Batch.CriterionCodes?.FirstOrDefault() ?? $"C{work.Batch.Index}",work.Batch.Topic,work.Batch.Query)],cancellationToken);
                evidence = found.Select((item,index)=>item with { Id=$"C{work.Batch.Index+1}-S{index+1}" }).ToArray();
            }
            evidence = AmveraAiChecklistSynthesisClient.SelectCriterionEvidence(evidence);
            IReadOnlyList<AiGeneratedChecklistItem> items = [];
            if (evidence.Count > 0)
            {
                logger.LogInformation("Запуск {RunId}, критерий {BatchIndex}: один запрос по {EvidenceCount} лучшим источникам.", work.RunId, work.Batch.Index, evidence.Count);
                await runStore.UpdateProgressAsync(work.RunId, work.Batch.Index, "generating", $"Найдено источников: {evidence.Count}. Модель формирует рабочий ответ", evidence.Count, string.Empty, cancellationToken);
                var streamed = new System.Text.StringBuilder();
                var savedLength = 0;
                var lastFlush = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var raw = await synthesisClient.SynthesizeStreamingAsync(work.Facility, evidence, async (delta, ct) =>
                    {
                        streamed.Append(delta);
                        if (streamed.Length - savedLength < 240 && lastFlush.Elapsed < TimeSpan.FromSeconds(1.5)) return;
                        savedLength = streamed.Length;
                        lastFlush.Restart();
                        await runStore.UpdateProgressAsync(work.RunId, work.Batch.Index, "generating", "Модель пишет рабочий ответ", evidence.Count, streamed.ToString(), ct);
                    }, cancellationToken);
                    await runStore.UpdateProgressAsync(work.RunId, work.Batch.Index, "validating", "Ответ получен. Проверяем формат и цитаты", evidence.Count, raw, cancellationToken);
                    items = AiChecklistOutputParser.Parse(raw, evidence).Items
                        .DistinctBy(item => item.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Take(1)
                        .ToArray();
                }
                catch (Exception exception) when (exception is JsonException or AiChecklistGenerationException)
                {
                    await runStore.UpdateProgressAsync(work.RunId, work.Batch.Index, "fallback", "Ответ не прошёл проверку. Используем базовый пункт классификатора", evidence.Count, streamed.ToString(), cancellationToken);
                    logger.LogWarning(exception, "Запуск {RunId}, критерий {BatchIndex}: уточнение ИИ отклонено, сохранён базовый пункт классификатора.", work.RunId, work.Batch.Index);
                }
            }
            else
            {
                await runStore.UpdateProgressAsync(work.RunId, work.Batch.Index, "fallback", "Источники не найдены. Используем базовый пункт классификатора", 0, string.Empty, cancellationToken);
            }
            await PersistOutcomeAsync(
                () => runStore.CompleteCriterionAsync(work.RunId, work.Batch.Index, evidence, items, timer.ElapsedMilliseconds, cancellationToken),
                work.RunId,
                work.Batch.Index,
                cancellationToken);
            logger.LogInformation("Запуск {RunId}, пакет {BatchIndex}: принято {ItemCount} пунктов за {DurationMs} мс.", work.RunId, work.Batch.Index, items.Count, timer.ElapsedMilliseconds);
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
        if (run.Batches.Any(item => item.Status is not ("completed" or "failed" or "skipped"))) return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Дождитесь завершения активных тематических пакетов.");
        if (!await runStore.BeginFinalizeAsync(runId, cancellationToken)) return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Финализация уже выполняется.");
        try
        {
            var classifierItems=AiChecklistCriterionConsolidator.Consolidate(run.Batches);
            IReadOnlyList<AiGeneratedDraftItem> draftItems;
            if(classifierItems.Count>0) draftItems=classifierItems;
            else
            {
                var items = run.Batches.SelectMany(batch => batch.Items).DistinctBy(item => item.Title.Trim(), StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
                var allEvidence = run.Evidence.Concat(run.Batches.SelectMany(batch=>batch.BatchEvidence ?? [])).DistinctBy(item=>item.Id,StringComparer.OrdinalIgnoreCase).ToArray();
                draftItems = ToDraftItems(items, allEvidence).Take(75).Concat(AiChecklistFallbackBuilder.Build(run.Facility)).DistinctBy(item => item.Title.Trim(), StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            }
            var result = await checklists.CreateAiDraftAsync(new(run.FacilityId, run.FacilityName, $"Автоматизированный чек-лист — {run.FacilityName}", draftItems, runId), cancellationToken);
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
