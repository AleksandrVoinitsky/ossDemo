internal static class ChecklistLifecycleChecks
{
    public static async Task RunAsync()
    {
        var repository = new InMemoryChecklistRepository();
        var service = new ChecklistService(repository);
        var facilityId = Guid.NewGuid();
        var request = new ChecklistTemplateWriteRequest("Шаблон", facilityId, 0,
            [new("Раздел", 1, [new("Исходный пункт", "Статья 1", "", 1)])]);

        var created = await service.CreateTemplateAsync(request, CancellationToken.None);
        AssertTrue(created.IsSuccess, "Шаблон должен создаваться.");
        var template = created.Value!;
        var draftResult = await service.CreateDraftAsync(new CreateChecklistRequest(template.Id, "Проверка", facilityId, null, null), CancellationToken.None);
        AssertTrue(draftResult.IsSuccess, "Черновик должен создаваться.");
        var draft = draftResult.Value!;

        var changed = request with
        {
            Name = "Изменённый шаблон",
            Version = template.Version,
            Sections = [new("Раздел", 1, [new("Изменённый пункт", "Статья 2", "", 1)])]
        };
        AssertTrue((await service.UpdateTemplateAsync(template.Id, changed, CancellationToken.None)).IsSuccess, "Шаблон должен обновляться.");
        AssertEqual("Исходный пункт", (await service.GetChecklistAsync(draft.Id, CancellationToken.None))!.Items[0].Title);

        var incompleteApproval = await service.ApproveAsync(draft.Id, "inspector", CancellationToken.None);
        AssertEqual("state_conflict", incompleteApproval.ErrorCode);

        var updatedItem = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("Да", "", "Проверено"), CancellationToken.None);
        AssertTrue(updatedItem.IsSuccess && updatedItem.Value!.Items[0].Result == "Да", "Результат пункта черновика должен обновляться.");
        var invalidResult = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("произвольный", "", ""), CancellationToken.None);
        AssertEqual("validation", invalidResult.ErrorCode);

        AssertTrue((await service.DeleteTemplateAsync(template.Id, CancellationToken.None)).IsSuccess, "Использованный шаблон должен удаляться.");
        AssertTrue(await service.GetChecklistAsync(draft.Id, CancellationToken.None) is not null, "Черновик должен сохраниться после удаления шаблона.");
        AssertTrue((await service.ApproveAsync(draft.Id, "inspector", CancellationToken.None)).IsSuccess, "Черновик должен утверждаться.");
        AssertEqual(0, (await service.ListDraftsAsync(CancellationToken.None)).Count);
        AssertTrue((await service.ListHistoryAsync(new ChecklistHistoryFilter(null, null, null, null), CancellationToken.None)).Any(x => x.Id == draft.Id), "Утверждённый чек-лист должен попасть в историю.");
        var rejected = await service.AddItemAsync(draft.Id, new AddChecklistItemRequest("Пункт", "Основание", "Раздел", ""), CancellationToken.None);
        AssertEqual("state_conflict", rejected.ErrorCode);
        var rejectedUpdate = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("Нет", "Нарушение", ""), CancellationToken.None);
        AssertEqual("state_conflict", rejectedUpdate.ErrorCode);
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
