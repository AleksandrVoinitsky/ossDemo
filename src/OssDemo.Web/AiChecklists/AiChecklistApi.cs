internal static class AiChecklistApi
{
    public static void MapAiChecklistApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai-checklists");
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
        group.MapPost("/runs", async (AiChecklistRequest request, AiChecklistAgent agent, CancellationToken ct) =>
        {
            var result = await agent.CreateRunAsync(request.FacilitySlug, ct);
            return result.IsSuccess
                ? Results.Created($"/api/ai-checklists/runs/{result.Value!.Id}", result.Value)
                : Error(result.ErrorCode, result.Error, result.Errors);
        });
        group.MapGet("/runs/{runId:guid}", async (Guid runId, AiChecklistAgent agent, CancellationToken ct) =>
        {
            var run = await agent.GetRunAsync(runId, ct);
            return run is null ? Results.NotFound(new { error = "Запуск ИИ-формирования не найден.", code = "not_found" }) : Results.Ok(run);
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
        _ => StatusCodes.Status400BadRequest
    };
}

internal sealed record AiChecklistRequest(string? FacilitySlug);
internal sealed record AiChecklistQueueRequest(IReadOnlyList<int> BatchIndexes);
