internal static class AiChecklistApi
{
    public static void MapAiChecklistApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai-checklists");
        group.MapPost("/drafts", async (CreateAiChecklistWorkflowDraftRequest request, IAiChecklistDraftStore drafts, CancellationToken ct) =>
            Results.Created("/api/ai-checklists/drafts", await drafts.CreateAsync(request.FacilitySlug?.Trim() ?? string.Empty, request.TemplateId, ct)));
        group.MapGet("/drafts/{draftId:guid}", async (Guid draftId, IAiChecklistDraftStore drafts, CancellationToken ct) =>
            await drafts.GetAsync(draftId, ct) is { } draft ? Results.Ok(draft) : Results.NotFound(new { error = "Черновик формирования не найден.", code = "not_found" }));
        group.MapPut("/drafts/{draftId:guid}", async (Guid draftId, UpdateAiChecklistDraftRequest request, IAiChecklistDraftStore drafts, CancellationToken ct) =>
            ToResult(await drafts.UpdateAsync(draftId, request, ct)));
        group.MapPost("/drafts/{draftId:guid}/documents", async (Guid draftId, HttpRequest request, IAiChecklistDraftStore drafts, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Error("validation", "Передайте файл и тип документа.", new Dictionary<string, string[]>());
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            return file is null
                ? Error("validation", "Выберите файл.", new Dictionary<string, string[]>())
                : ToResult(await drafts.AddDocumentAsync(draftId, form["type"].ToString(), file, ct));
        }).DisableAntiforgery();
        group.MapDelete("/drafts/{draftId:guid}/documents/{documentId:guid}", async (Guid draftId, Guid documentId, IAiChecklistDraftStore drafts, CancellationToken ct) =>
        {
            var result = await drafts.DeleteDocumentAsync(draftId, documentId, ct);
            return result.IsSuccess ? Results.NoContent() : Error(result.ErrorCode, result.Error, result.Errors);
        });
        group.MapPost("/analyze", async (AiChecklistRequest request, AiChecklistAgent agent, CancellationToken ct) =>
            ToResult(await agent.AnalyzeAsync(request.FacilitySlug, ct)));
        group.MapPost("/search", async (AiChecklistRequest request, AiChecklistAgent agent, CancellationToken ct) =>
            ToResult(await agent.SearchAsync(request.FacilitySlug, ct)));
        group.MapPost("/generate", async (AiChecklistRequest request, AiChecklistAgent agent, CancellationToken ct) =>
        {
            var result = await agent.GenerateAsync(request.FacilitySlug, ct);
            return result.IsSuccess
                ? Results.Created($"/api/checklists/{result.Value!.Id}", result.Value)
                : Error(result.ErrorCode, result.Error, result.Errors);
        });
        group.MapPost("/runs", async (CreateAiChecklistRunRequest request, AiChecklistAgent agent, IAiChecklistDraftStore drafts, CancellationToken ct) =>
        {
            AiChecklistDraftState? draft = null;
            if (request.DraftId != Guid.Empty)
            {
                draft = await drafts.GetAsync(request.DraftId, ct);
                if (draft is null) return Error("not_found", "Черновик формирования не найден.", new Dictionary<string, string[]>());
                if (draft.RunId is { } existingRunId && await agent.GetRunAsync(existingRunId, ct) is { } existingRun)
                    return Results.Ok(AiChecklistRunResponse.From(existingRun));
            }
            var result = await agent.CreateRunAsync(draft?.FacilitySlug ?? request.FacilitySlug, ct);
            if (result.IsSuccess && draft is not null)
            {
                var attached = await drafts.AttachRunAsync(draft.Id, result.Value!.Id, ct);
                if (!attached.IsSuccess) return Error(attached.ErrorCode, attached.Error, attached.Errors);
            }
            return result.IsSuccess
                ? Results.Created($"/api/ai-checklists/runs/{result.Value!.Id}", AiChecklistRunResponse.From(result.Value))
                : Error(result.ErrorCode, result.Error, result.Errors);
        });
        group.MapGet("/runs/{runId:guid}", async (Guid runId, AiChecklistAgent agent, CancellationToken ct) =>
        {
            var run = await agent.GetRunAsync(runId, ct);
            return run is null ? Results.NotFound(new { error = "Запуск ИИ-формирования не найден.", code = "not_found" }) : Results.Ok(AiChecklistRunResponse.From(run));
        });
        group.MapGet("/runs/{runId:guid}/traces", async (Guid runId, int offset, int limit, AiChecklistAgent agent, CancellationToken ct) =>
        {
            var page = await agent.GetTracePageAsync(runId, offset, limit == 0 ? 50 : limit, ct);
            return page is null ? Results.NotFound(new { error = "Запуск ИИ-формирования не найден.", code = "not_found" }) : Results.Ok(page);
        });
        group.MapPost("/runs/{runId:guid}/batches/{batchIndex:int}", async (Guid runId, int batchIndex, AiChecklistAgent agent, CancellationToken ct) =>
        {
            try
            {
                return await agent.QueueBatchAsync(runId, batchIndex, ct)
                    ? Results.Accepted($"/api/ai-checklists/runs/{runId}")
                    : Results.NotFound(new { error = "Пакет ИИ-формирования не найден или запуск уже завершается.", code = "not_found" });
            }
            catch (Npgsql.NpgsqlException)
            {
                return Error("storage_unavailable", "База данных временно занята. Повторите запуск пакета.", new Dictionary<string, string[]>());
            }
        });
        group.MapPost("/runs/{runId:guid}/queue", async (Guid runId, AiChecklistQueueRequest request, AiChecklistAgent agent, CancellationToken ct) =>
        {
            try
            {
                return await agent.QueueBatchesAsync(runId, request.BatchIndexes ?? [], ct)
                    ? Results.Accepted($"/api/ai-checklists/runs/{runId}")
                    : Results.NotFound(new { error = "Пакеты ИИ-формирования не найдены или запуск уже завершается.", code = "not_found" });
            }
            catch (Npgsql.NpgsqlException)
            {
                return Error("storage_unavailable", "База данных временно занята. Повторите постановку пакетов.", new Dictionary<string, string[]>());
            }
        });
        group.MapPost("/runs/{runId:guid}/stop", async (Guid runId, AiChecklistAgent agent, CancellationToken ct) =>
        {
            try
            {
                return await agent.StopAsync(runId, ct)
                    ? Results.Accepted($"/api/ai-checklists/runs/{runId}")
                    : Results.Conflict(new { error = "Формирование уже завершено или перешло к сохранению.", code = "state_conflict" });
            }
            catch (Npgsql.NpgsqlException)
            {
                return Error("storage_unavailable", "Не удалось остановить формирование. Повторите попытку.", new Dictionary<string, string[]>());
            }
        });
        group.MapPost("/runs/{runId:guid}/finalize", async (Guid runId, AiChecklistAgent agent, CancellationToken ct) =>
        {
            var result = await agent.FinalizeRunAsync(runId, ct);
            return result.IsSuccess
                ? Results.Created($"/api/checklists/{result.Value!.Id}", result.Value)
                : Error(result.ErrorCode, result.Error, result.Errors);
        });
    }

    private static IResult ToResult<T>(ChecklistOperationResult<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Error(result.ErrorCode, result.Error, result.Errors);

    private static IResult Error(string? code, string? message, IReadOnlyDictionary<string, string[]> errors) =>
        Results.Json(new { error = message, code, fields = errors }, statusCode: StatusCode(code));

    internal static int StatusCode(string? code) => code switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "search_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "storage_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "ai_unavailable" => StatusCodes.Status502BadGateway,
        "state_conflict" => StatusCodes.Status409Conflict,
        "version_conflict" => StatusCodes.Status409Conflict,
        "facility_profile_incomplete" => StatusCodes.Status409Conflict,
        "coverage_gap" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest
    };
}

internal sealed record AiChecklistRequest(string? FacilitySlug);
internal sealed record AiChecklistQueueRequest(IReadOnlyList<int> BatchIndexes);
