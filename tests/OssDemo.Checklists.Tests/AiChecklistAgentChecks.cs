internal static class AiChecklistAgentChecks
{
    public static void RunDomainChecks()
    {
        var profile = new FacilityProfile("bereznikovskoe", new FacilityProfileFields
        {
            ShortName = "Березниковское ЛПУМГ",
            FullName = "Березниковское линейное управление",
            Type = "Магистральный газопровод",
            Category = "I категория",
            Region = "Пермский край",
            EnvironmentalAspects = "Выбросы в атмосферу\nОбращение с отходами",
            Equipment = "Газоперекачивающие агрегаты",
            EmissionSources = "Газотурбинная установка",
            Permits = "Комплексное экологическое разрешение"
        }, null, null);

        var queries = AiChecklistQueryPlanner.Build(profile);
        AssertTrue(queries.Count is > 3 and <= 8, "Планировщик должен создавать ограниченный набор запросов.");
        AssertTrue(queries.Select(item => item.Query).Distinct(StringComparer.OrdinalIgnoreCase).Count() == queries.Count, "Поисковые запросы не должны повторяться.");
        AssertTrue(queries.Any(item => item.Query.Contains("выброс", StringComparison.OrdinalIgnoreCase)), "Экологические аспекты должны попадать в поиск.");
        AssertTrue(queries.All(item => !item.Query.Contains("Не указано", StringComparison.OrdinalIgnoreCase)), "Пустые признаки не должны попадать в поиск.");

        var evidence = new[]
        {
            new AiChecklistEvidence("S1", "Атмосферный воздух", "ФЗ-7", "Статья 67", "Проверить программу ПЭК", 0.9),
            new AiChecklistEvidence("S2", "Отходы", "Приказ 1028", "Раздел 4", "Проверить места накопления отходов", 0.8)
        };
        var json = """
            {"name":"ИИ-проверка","items":[
              {"section":"Общие вопросы","title":"Проверить программу ПЭК","reason":"Объект I категории","confidence":0.91,"sourceIds":["S1"]},
              {"section":"Общие вопросы","title":" Проверить программу ПЭК ","reason":"Дубликат","confidence":0.5,"sourceIds":["S1"]},
              {"section":"Отходы","title":"Неподтверждённый пункт","reason":"Нет источника","confidence":0.7,"sourceIds":["S404"]}
            ]}
            """;
        var parsed = AiChecklistOutputParser.Parse(json, evidence);
        AssertEqual("ИИ-проверка", parsed.Name);
        AssertEqual(1, parsed.Items.Count);
        AssertEqual("S1", parsed.Items[0].SourceIds[0]);

        var invalid = AiChecklistOutputParser.Parse("{\"name\":\"x\",\"items\":[{\"title\":\"Без ссылки\",\"sourceIds\":[]}]}", evidence);
        AssertEqual(0, invalid.Items.Count);
    }

    public static async Task RunPersistenceChecksAsync()
    {
        var repository = new InMemoryChecklistRepository();
        var facilityId = Guid.NewGuid();
        var result = await repository.CreateAiDraftAsync(new(
            facilityId,
            "Березниковское ЛПУМГ",
            "ИИ-проверка",
            [new("Атмосферный воздух", "Проверить программу ПЭК", "ФЗ-7 — Статья 67", "Источник: S1")]),
            CancellationToken.None);

        AssertTrue(result.IsSuccess, "Подтверждённые ИИ-пункты должны сохраняться как черновик.");
        AssertEqual("draft", result.Value!.Status);
        AssertEqual("ИИ · карточка объекта", result.Value.TemplateName);
        AssertEqual("ai", result.Value.Items[0].Origin);
        AssertEqual<Guid?>(null, result.Value.TemplateId);
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
