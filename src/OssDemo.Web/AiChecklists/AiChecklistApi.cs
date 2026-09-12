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
    }

    private static IResult ToResult<T>(ChecklistOperationResult<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Error(result.ErrorCode, result.Error, result.Errors);

    private static IResult Error(string? code, string? message, IReadOnlyDictionary<string, string[]> errors) =>
        Results.Json(new { error = message, code, fields = errors }, statusCode: StatusCode(code));

    private static int StatusCode(string? code) => code switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "search_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "ai_unavailable" => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status400BadRequest
    };
}

internal sealed record AiChecklistRequest(string? FacilitySlug);
