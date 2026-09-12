var facilityId = Guid.NewGuid();
var invalid = ChecklistRules.NormalizeTemplate(new ChecklistTemplateWriteRequest(" ", Guid.Empty, 0, []));
AssertTrue(!invalid.IsSuccess, "Пустой шаблон должен быть отклонён.");
AssertTrue(invalid.Errors.ContainsKey("name"), "Ожидалась ошибка названия.");
AssertTrue(invalid.Errors.ContainsKey("facilityId"), "Ожидалась ошибка объекта.");
AssertTrue(invalid.Errors.ContainsKey("sections"), "Ожидалась ошибка разделов.");

var valid = ChecklistRules.NormalizeTemplate(new ChecklistTemplateWriteRequest(
    "  Проверка ПЭК  ", facilityId, 3,
    [new ChecklistTemplateSectionWrite("  Общие вопросы  ", 9,
        [new ChecklistTemplateItemWrite("  Позиция  ", "  Статья 1  ", "  Комментарий  ", 7)])]));

AssertTrue(valid.IsSuccess, "Корректный шаблон должен пройти проверку.");
AssertEqual("Проверка ПЭК", valid.Value!.Name);
AssertEqual("Общие вопросы", valid.Value.Sections[0].Title);
AssertEqual(1, valid.Value.Sections[0].Position);
AssertEqual(1, valid.Value.Sections[0].Items[0].Position);
AssertEqual("Статья 1", valid.Value.Sections[0].Items[0].Basis);

AssertEqual(3, ChecklistSeedData.Templates.Count);
AssertEqual(3, ChecklistSeedData.History.Count);
AssertTrue(ChecklistSeedData.History.All(item => item.Status == ChecklistStatus.Approved), "История должна быть утверждена.");
AssertTrue(ChecklistSeedData.Templates.Select(item => item.Id).Distinct().Count() == 3, "Идентификаторы шаблонов должны быть уникальны.");
AssertTrue(ChecklistSeedData.History.SelectMany(item => item.Items).All(item => !string.IsNullOrWhiteSpace(item.Title)), "История не должна содержать пустые пункты.");

await ChecklistLifecycleChecks.RunAsync();

AssertEqual(400, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("validation", "Проверьте поля.")));
AssertEqual(404, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("not_found", "Не найдено.")));
AssertEqual(409, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("version_conflict", "Версия изменилась.")));

Console.WriteLine("Checklist domain checks passed.");

static void AssertTrue(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}
