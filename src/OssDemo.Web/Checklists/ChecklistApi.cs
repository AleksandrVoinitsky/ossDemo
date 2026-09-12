internal static class ChecklistApi
{
    public static void MapChecklistApi(this WebApplication app)
    {
        app.MapGet("/api/checklist-templates", async (ChecklistService service, CancellationToken ct) => Results.Ok(await service.ListTemplatesAsync(ct)));
        app.MapGet("/api/checklist-templates/{id:guid}", async (Guid id, ChecklistService service, CancellationToken ct) =>
            await service.GetTemplateAsync(id, ct) is { } value ? Results.Ok(value) : Results.NotFound(new { error = "Шаблон не найден.", code = "not_found" }));
        app.MapPost("/api/checklist-templates", async (ChecklistTemplateWriteRequest request, ChecklistService service, CancellationToken ct) =>
            ChecklistApiResponses.ToCreatedResult(await service.CreateTemplateAsync(request, ct), value => $"/api/checklist-templates/{value.Id}"));
        app.MapPut("/api/checklist-templates/{id:guid}", async (Guid id, ChecklistTemplateWriteRequest request, ChecklistService service, CancellationToken ct) =>
            ChecklistApiResponses.ToResult(await service.UpdateTemplateAsync(id, request, ct)));
        app.MapPost("/api/checklist-templates/{id:guid}/copy", async (Guid id, CopyChecklistTemplateRequest request, ChecklistService service, CancellationToken ct) =>
            ChecklistApiResponses.ToCreatedResult(await service.CopyTemplateAsync(id, request.Name, ct), value => $"/api/checklist-templates/{value.Id}"));
        app.MapDelete("/api/checklist-templates/{id:guid}", async (Guid id, ChecklistService service, CancellationToken ct) =>
        {
            var result = await service.DeleteTemplateAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : ChecklistApiResponses.ToResult(result);
        });

        app.MapGet("/api/checklists/drafts", async (ChecklistService service, CancellationToken ct) => Results.Ok(await service.ListDraftsAsync(ct)));
        app.MapGet("/api/checklists/history", async (string? search, Guid? facilityId, DateOnly? from, DateOnly? to, ChecklistService service, CancellationToken ct) =>
            Results.Ok(await service.ListHistoryAsync(new(search, facilityId, from, to), ct)));
        app.MapPost("/api/checklists", async (CreateChecklistRequest request, ChecklistService service, CancellationToken ct) =>
            ChecklistApiResponses.ToCreatedResult(await service.CreateDraftAsync(request, ct), value => $"/api/checklists/{value.Id}"));
        app.MapGet("/api/checklists/{id:guid}", async (Guid id, ChecklistService service, CancellationToken ct) =>
            await service.GetChecklistAsync(id, ct) is { } value ? Results.Ok(value) : Results.NotFound(new { error = "Чек-лист не найден.", code = "not_found" }));
        app.MapPost("/api/checklists/{id:guid}/items", async (Guid id, AddChecklistItemRequest request, ChecklistService service, CancellationToken ct) =>
            ChecklistApiResponses.ToResult(await service.AddItemAsync(id, request, ct)));
        app.MapPost("/api/checklists/{id:guid}/approve", async (Guid id, ChecklistService service, CancellationToken ct) =>
            ChecklistApiResponses.ToResult(await service.ApproveAsync(id, OssDemo.Web.Pages.LoginModel.UserName, ct)));

        app.MapGet("/exports/checklists/{id:guid}.{format}", async (Guid id, string format, ChecklistService service, CancellationToken ct) =>
        {
            var checklist = await service.GetChecklistAsync(id, ct);
            if (checklist is null) return Results.NotFound(new { error = "Чек-лист не найден." });
            var export = format.ToLowerInvariant() switch
            {
                "xlsx" => ChecklistExportFiles.CreateXlsx(checklist),
                "docx" => ChecklistExportFiles.CreateDocx(checklist),
                "pdf" => ChecklistExportFiles.CreatePdf(checklist),
                _ => null
            };
            return export is null
                ? Results.BadRequest(new { error = "Формат экспорта не поддерживается." })
                : Results.File(export.Content, export.ContentType, export.FileName);
        });
    }
}
