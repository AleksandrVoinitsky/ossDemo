internal static class ClassifierApi
{
    public static void MapClassifierApi(this WebApplication app)
    {
        var group=app.MapGroup("/api/classifier");
        group.MapGet("/", async (IClassifierRepository repository,CancellationToken ct)=>Results.Ok(await repository.GetTreeAsync(ct)));
        group.MapPost("/criteria", async (ClassifierCriterionCreate request,IClassifierRepository repository,CancellationToken ct)=>ToResult(await repository.CreateCriterionAsync(request,ct),true));
        group.MapPut("/criteria/{id:guid}", async (Guid id,ClassifierCriterionWrite request,IClassifierRepository repository,CancellationToken ct)=>ToResult(await repository.UpdateCriterionAsync(id,request,ct),false));
        group.MapDelete("/criteria/{id:guid}", async (Guid id,IClassifierRepository repository,CancellationToken ct)=>await repository.DeleteCriterionAsync(id,ct)?Results.NoContent():Results.NotFound());
    }

    private static IResult ToResult(ChecklistOperationResult<ClassifierCriterion> result,bool created)=>result.IsSuccess
        ? created?Results.Created($"/api/classifier/criteria/{result.Value!.Id}",result.Value):Results.Ok(result.Value)
        : Results.Json(new{error=result.Error,code=result.ErrorCode,fields=result.Errors},statusCode:result.ErrorCode=="not_found"?404:400);
}
