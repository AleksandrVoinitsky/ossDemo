internal static class RequirementApi
{
    public static void MapRequirementApi(this WebApplication app)
    {
        app.MapGet("/api/classifier/criteria/{criterionId:guid}/requirements", async (
            Guid criterionId, IRequirementWorkspace workspace, CancellationToken ct) =>
        {
            var result = await workspace.ResolveCriterionAsync(criterionId, ct);
            return result is null ? Results.NotFound(new { error = "Критерий не найден.", code = "not_found" }) : Results.Ok(result);
        });
        app.MapGet("/api/requirements", async (string? search, int? limit, IRequirementWorkspace workspace, CancellationToken ct) =>
            Results.Ok(await workspace.SearchAsync(search, limit ?? 30, ct)));
        app.MapPut("/api/requirements/{requirementId}/revision", async (
            string requirementId, RequirementRevisionWrite request, IRequirementWorkspace workspace, CancellationToken ct) =>
            ToResult(await workspace.SaveRevisionAsync(requirementId, request, OssDemo.Web.Pages.LoginModel.UserName, ct)));
        app.MapPut("/api/classifier/criteria/{criterionId:guid}/requirements/{requirementId}", async (
            Guid criterionId, string requirementId, RequirementLinkWrite request, IRequirementWorkspace workspace, CancellationToken ct) =>
            ToResult(await workspace.SetLinkAsync(criterionId, requirementId, request.Action, OssDemo.Web.Pages.LoginModel.UserName, ct)));
        app.MapDelete("/api/classifier/criteria/{criterionId:guid}/requirements/{requirementId}", async (
            Guid criterionId, string requirementId, IRequirementWorkspace workspace, CancellationToken ct) =>
            ToResult(await workspace.ClearLinkAsync(criterionId, requirementId, OssDemo.Web.Pages.LoginModel.UserName, ct)));
    }

    private static IResult ToResult<T>(ChecklistOperationResult<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.Json(new { error = result.Error, code = result.ErrorCode, fields = result.Errors }, statusCode: ChecklistApiResponses.StatusCode(result));
}
