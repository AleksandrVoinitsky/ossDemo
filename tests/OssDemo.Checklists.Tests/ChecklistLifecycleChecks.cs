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

        var updatedItem = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("Да", "", "Проверено"), CancellationToken.None);
        AssertTrue(updatedItem.IsSuccess && updatedItem.Value!.Items[0].Result == "Да", "Результат пункта черновика должен обновляться.");
        var clearedResult = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("", "", "Черновое примечание"), CancellationToken.None);
        AssertTrue(clearedResult.IsSuccess && clearedResult.Value!.Items[0].Result == "", "Автосохранение должно принимать ещё не выбранный результат.");
        AssertTrue((await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("Да", "", "Проверено"), CancellationToken.None)).IsSuccess, "Результат должен повторно сохраняться перед утверждением.");
        var invalidResult = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("произвольный", "", ""), CancellationToken.None);
        AssertEqual("validation", invalidResult.ErrorCode);

        var editedItem = await service.EditItemAsync(draft.Id, draft.Items[0].Id,
            new EditChecklistItemRequest("Обновлённый пункт", "Статья 2", "Новый раздел", "Новое примечание"), CancellationToken.None);
        AssertTrue(editedItem.IsSuccess, "Содержание пункта черновика должно редактироваться.");
        AssertEqual("Обновлённый пункт", editedItem.Value!.Items[0].Title);
        AssertEqual("Статья 2", editedItem.Value.Items[0].Basis);
        AssertEqual("Новый раздел", editedItem.Value.Items[0].Section);
        AssertEqual("Новое примечание", editedItem.Value.Items[0].Note);
        var invalidEdit = await service.EditItemAsync(draft.Id, draft.Items[0].Id,
            new EditChecklistItemRequest(" ", "Статья 2", "Раздел", ""), CancellationToken.None);
        AssertEqual("validation", invalidEdit.ErrorCode);

        var addedItem = await service.AddItemAsync(draft.Id, new AddChecklistItemRequest("Временный пункт", "Основание", "Раздел", ""), CancellationToken.None);
        AssertTrue(addedItem.IsSuccess && addedItem.Value!.Items.Count == 2, "Пункт должен добавляться перед проверкой удаления.");
        var addedChecklist = addedItem.Value!;
        var deletedItem = await service.DeleteItemAsync(draft.Id, addedChecklist.Items[1].Id, CancellationToken.None);
        AssertTrue(deletedItem.IsSuccess && deletedItem.Value!.Items.Count == 1, "Пункт черновика должен удаляться.");
        var checklistAfterDelete = deletedItem.Value!;
        AssertEqual(1, checklistAfterDelete.Items[0].Position);
        var disposableDraft = await service.CreateDraftAsync(new CreateChecklistRequest(template.Id, "Удаляемый черновик", facilityId, null, null), CancellationToken.None);
        AssertTrue(disposableDraft.IsSuccess, "Черновик для удаления должен создаваться.");
        var problemItem = await service.EditItemAsync(disposableDraft.Value!.Id, disposableDraft.Value.Items[0].Id,
            new EditChecklistItemRequest("Пункт без основания", "Основание требует проверки по актуальной базе знаний.", "Раздел", "Заполнить вручную"), CancellationToken.None);
        AssertTrue(problemItem.IsSuccess && problemItem.Value!.Items[0].NeedsBasisReview, "Неподтверждённое основание должно помечаться как проблемное.");
        AssertEqual("state_conflict", (await service.ApproveAsync(disposableDraft.Value.Id, "inspector", CancellationToken.None)).ErrorCode);

        AssertTrue((await service.DeleteTemplateAsync(template.Id, CancellationToken.None)).IsSuccess, "Использованный шаблон должен удаляться.");
        AssertTrue(await service.GetChecklistAsync(draft.Id, CancellationToken.None) is not null, "Черновик должен сохраниться после удаления шаблона.");
        AssertTrue((await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("", "", ""), CancellationToken.None)).IsSuccess, "Результат проверки объекта не должен требоваться для утверждения состава чек-листа.");
        AssertTrue((await service.ApproveAsync(draft.Id, "inspector", CancellationToken.None)).IsSuccess, "Черновик должен утверждаться.");
        AssertEqual(1, (await service.ListDraftsAsync(CancellationToken.None)).Count);
        AssertTrue((await service.ListHistoryAsync(new ChecklistHistoryFilter(null, null, null, null), CancellationToken.None)).Any(x => x.Id == draft.Id), "Утверждённый чек-лист должен попасть в историю.");
        var rejected = await service.AddItemAsync(draft.Id, new AddChecklistItemRequest("Пункт", "Основание", "Раздел", ""), CancellationToken.None);
        AssertEqual("state_conflict", rejected.ErrorCode);
        var rejectedUpdate = await service.UpdateItemAsync(draft.Id, draft.Items[0].Id, new UpdateChecklistItemRequest("Нет", "Нарушение", ""), CancellationToken.None);
        AssertEqual("state_conflict", rejectedUpdate.ErrorCode);
        var rejectedEdit = await service.EditItemAsync(draft.Id, draft.Items[0].Id, new EditChecklistItemRequest("Пункт", "Основание", "Раздел", ""), CancellationToken.None);
        AssertEqual("state_conflict", rejectedEdit.ErrorCode);
        var rejectedDelete = await service.DeleteItemAsync(draft.Id, draft.Items[0].Id, CancellationToken.None);
        AssertEqual("state_conflict", rejectedDelete.ErrorCode);
        var rejectedChecklistDelete = await service.DeleteDraftAsync(draft.Id, CancellationToken.None);
        AssertEqual("state_conflict", rejectedChecklistDelete.ErrorCode);

        var deletedDraft = await service.DeleteDraftAsync(disposableDraft.Value!.Id, CancellationToken.None);
        AssertTrue(deletedDraft.IsSuccess, "Незавершённый чек-лист должен удаляться.");
        AssertTrue(await service.GetChecklistAsync(disposableDraft.Value.Id, CancellationToken.None) is null, "Удалённый черновик не должен оставаться в хранилище.");
        AssertEqual(0, (await service.ListDraftsAsync(CancellationToken.None)).Count);
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
