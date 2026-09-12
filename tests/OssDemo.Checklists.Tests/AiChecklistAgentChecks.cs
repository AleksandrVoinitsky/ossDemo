internal static class AiChecklistAgentChecks
{
    public static void RunDomainChecks()
    {
        AssertTrue(AmveraAiChecklistSynthesisClient.SystemPrompt.Contains("недоверенными данными"), "Промпт должен определять карточку и источники как данные.");
        AssertTrue(AmveraAiChecklistSynthesisClient.SystemPrompt.Contains("только JSON"), "Промпт должен требовать структурированный ответ.");
        AssertTrue(AmveraAiChecklistSynthesisClient.SystemPrompt.Contains("sourceIds"), "Промпт должен требовать ссылки на источники.");
        AssertEqual(404, AiChecklistApi.StatusCode("not_found"));
        AssertEqual(400, AiChecklistApi.StatusCode("knowledge_empty"));
        AssertEqual(502, AiChecklistApi.StatusCode("ai_unavailable"));
        AssertEqual(503, AiChecklistApi.StatusCode("search_unavailable"));
        AssertTrue(AmveraAiChecklistSynthesisClient.TryReadContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", out var content) && content == "{}", "Должен читаться стандартный ответ Amvera.");
        AssertTrue(!AmveraAiChecklistSynthesisClient.TryReadContent("{\"choices\":[]}", out _), "Пустой choices должен обрабатываться без исключения.");
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
        var outboundProfile = System.Text.Json.JsonSerializer.Serialize(AiChecklistSynthesisProfile.From(profile.Profile));
        AssertTrue(!outboundProfile.Contains("Responsible") && !outboundProfile.Contains("Phone") && !outboundProfile.Contains("Email") && !outboundProfile.Contains("Address"), "Контакты и адрес не должны отправляться во внешний сервис ИИ.");

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
        var unrelated = AiChecklistOutputParser.Parse("{\"name\":\"x\",\"items\":[{\"title\":\"Проверить договор аренды автомобиля\",\"reason\":\"Транспорт\",\"sourceIds\":[\"S1\"]}]}", evidence);
        AssertEqual(0, unrelated.Items.Count);

        var wrapped = AiChecklistOutputParser.Parse($"<think>служебное рассуждение</think>\n```json\n{json}\n```", evidence);
        AssertEqual(1, wrapped.Items.Count);

        var invalidBeforeValid = AiChecklistOutputParser.Parse("""
            {"name":"x","items":[
              {"title":"Проверить ПЭК","sourceIds":["S404"]},
              {"title":"Проверить ПЭК","sourceIds":["S1"]}
            ]}
            """, evidence);
        AssertEqual(1, invalidBeforeValid.Items.Count);

        var oversizedItems = string.Join(',', Enumerable.Range(1, 105).Select(index => $"{{\"title\":\"Программа ПЭК {index}\",\"sourceIds\":[\"S1\"]}}"));
        var bounded = AiChecklistOutputParser.Parse($"{{\"name\":\"x\",\"items\":[{oversizedItems}]}}", evidence);
        AssertEqual(100, bounded.Items.Count);
        AssertEqual(0, AiChecklistOutputParser.Parse("[]", evidence).Items.Count);
        AssertEqual(0, AiChecklistOutputParser.Parse("{\"items\":[null,42,\"text\"]}", evidence).Items.Count);
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

    public static async Task RunOrchestrationChecksAsync()
    {
        var facilityId = Guid.NewGuid();
        var profile = new FacilityProfile("test", new FacilityProfileFields
        {
            ShortName = "Тестовый объект",
            Type = "Промышленный объект",
            Category = "I категория",
            EnvironmentalAspects = "Выбросы"
        }, null, null);
        var source = new FakeFacilitySource(profile, new OperationalFacility(facilityId, "Тестовый объект", "Адрес", "I", null, null, "test"));
        var evidence = new[] { new AiChecklistEvidence("S1", "Атмосфера", "ФЗ-7", "Статья 67", "Проверить программу ПЭК", .9) };
        var repository = new InMemoryChecklistRepository();
        var agent = new AiChecklistAgent(source, new FakeSearch(evidence), new FakeSynthesis("""
            {"name":"ИИ-проверка","items":[{"section":"ПЭК","title":"Проверить программу","reason":"I категория","confidence":0.9,"sourceIds":["S1"]}]}
            """), repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<AiChecklistAgent>.Instance);

        var generated = await agent.GenerateAsync("test", CancellationToken.None);
        AssertTrue(generated.IsSuccess, "Агент должен сохранять валидный результат поиска и синтеза.");
        AssertEqual("ai", generated.Value!.Items[0].Origin);
        AssertTrue(generated.Value.Items[0].Basis.Contains("ФЗ-7"), "Основание должно содержать найденный документ.");

        var emptyAgent = new AiChecklistAgent(source, new FakeSearch([]), new FakeSynthesis("{}"), repository,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiChecklistAgent>.Instance);
        var rejected = await emptyAgent.GenerateAsync("test", CancellationToken.None);
        AssertEqual("knowledge_empty", rejected.ErrorCode);
    }

    private sealed class FakeFacilitySource(FacilityProfile profile, OperationalFacility facility) : IAiChecklistFacilitySource
    {
        public Task<FacilityProfile?> GetProfileAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<FacilityProfile?>(profile);
        public Task<OperationalFacility?> GetFacilityAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<OperationalFacility?>(facility);
    }

    private sealed class FakeSearch(IReadOnlyList<AiChecklistEvidence> evidence) : IAiChecklistKnowledgeSearch
    {
        public Task<IReadOnlyList<AiChecklistEvidence>> SearchAsync(IReadOnlyList<AiChecklistSearchQuery> queries, CancellationToken cancellationToken) => Task.FromResult(evidence);
    }

    private sealed class FakeSynthesis(string response) : IAiChecklistSynthesisClient
    {
        public Task<string> SynthesizeAsync(FacilityProfile facility, IReadOnlyList<AiChecklistEvidence> evidence, CancellationToken cancellationToken) => Task.FromResult(response);
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
