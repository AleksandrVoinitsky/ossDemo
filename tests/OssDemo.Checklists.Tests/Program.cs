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
var localTimestamp = new DateTimeOffset(2026, 9, 12, 22, 18, 0, TimeSpan.FromHours(5));
var postgresTimestamp = ChecklistDatabaseInitializer.ToPostgresTimestamp(localTimestamp);
AssertEqual(DateTimeKind.Utc, postgresTimestamp.Kind);
AssertEqual(localTimestamp.UtcDateTime, postgresTimestamp);

await ChecklistLifecycleChecks.RunAsync();
AiChecklistAgentChecks.RunDomainChecks();
await AiChecklistAgentChecks.RunPersistenceChecksAsync();
await AiChecklistAgentChecks.RunOrchestrationChecksAsync();
AiChecklistAgentChecks.RunBatchPlanningChecks();
await AiChecklistRunChecks.RunAsync();

AssertEqual(400, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("validation", "Проверьте поля.")));
AssertEqual(404, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("not_found", "Не найдено.")));
AssertEqual(409, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("version_conflict", "Версия изменилась.")));

var exportChecklist = new ChecklistDetails(
    Guid.NewGuid(), "Проверка объекта", facilityId, "Березниковское ЛПУМГ", null, "Шаблон ПЭК", "approved",
    new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow, "Инспектор",
    [new ChecklistItemDetails(Guid.NewGuid(), 1, "Общие вопросы", "Проверить программу ПЭК", "ФЗ-7", "Да", "", "Актуально", "template")]);
var xlsx = ChecklistExportFiles.CreateXlsx(exportChecklist);
var docx = ChecklistExportFiles.CreateDocx(exportChecklist);
var pdf = ChecklistExportFiles.CreatePdf(exportChecklist);
AssertTrue(xlsx.Content.Length > 100 && xlsx.FileName.EndsWith(".xlsx"), "Ожидался XLSX с данными чек-листа.");
AssertTrue(docx.Content.Length > 100 && docx.FileName.EndsWith(".docx"), "Ожидался DOCX с данными чек-листа.");
AssertTrue(ReadZipEntry(xlsx.Content, "xl/worksheets/sheet1.xml").Contains("Проверка объекта"), "XLSX должен содержать название чек-листа.");
AssertTrue(ReadZipEntry(docx.Content, "word/document.xml").Contains("Проверить программу ПЭК"), "DOCX должен содержать пункты чек-листа.");
AssertTrue(System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 8).StartsWith("%PDF"), "Ожидался PDF-документ.");
var manyItems = exportChecklist with { Items = Enumerable.Range(1, 80).Select(index => exportChecklist.Items[0] with { Id = Guid.NewGuid(), Position = index, Title = $"Пункт {index}" }).ToArray() };
var completePdfText = System.Text.Encoding.ASCII.GetString(ChecklistExportFiles.CreatePdf(manyItems).Content);
AssertTrue(completePdfText.Contains("80."), "PDF не должен обрезать длинный чек-лист.");
AssertTrue(completePdfText.Contains("Basis: FZ-7") && completePdfText.Contains("Note: Aktualno"), "PDF должен содержать фактические поля пункта.");

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

static string ReadZipEntry(byte[] content, string path)
{
    using var stream = new MemoryStream(content);
    using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
    using var reader = new StreamReader(archive.GetEntry(path)!.Open());
    return reader.ReadToEnd();
}
