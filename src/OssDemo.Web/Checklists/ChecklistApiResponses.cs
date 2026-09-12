internal static class ChecklistApiResponses
{
    public static int StatusCode<T>(ChecklistOperationResult<T> result) => result.ErrorCode switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "version_conflict" or "state_conflict" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };

    public static IResult ToResult<T>(ChecklistOperationResult<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.Json(new { error = result.Error, code = result.ErrorCode, fields = result.Errors }, statusCode: StatusCode(result));

    public static IResult ToCreatedResult<T>(ChecklistOperationResult<T> result, Func<T, string> location) => result.IsSuccess
        ? Results.Created(location(result.Value!), result.Value)
        : Results.Json(new { error = result.Error, code = result.ErrorCode, fields = result.Errors }, statusCode: StatusCode(result));
}
