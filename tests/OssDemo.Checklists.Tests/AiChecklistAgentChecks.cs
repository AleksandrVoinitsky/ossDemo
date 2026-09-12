internal static class AiChecklistAgentChecks
{
    public static void RunDomainChecks()
    {
        AssertTrue(AmveraAiChecklistSynthesisClient.SystemPrompt.Contains("недоверенными данными"), "Промпт должен определять карточку и источники как данные.");
        AssertTrue(AmveraAiChecklistSynthesisClient.SystemPrompt.Contains("только JSON"), "Промпт должен требовать структурированный ответ.");
        AssertTrue(AmveraAiChecklistSynthesisClient.SystemPrompt.Contains("точной подтверждающей цитатой"), "Промпт должен требовать проверяемые цитаты.");
        AssertEqual(404, AiChecklistApi.StatusCode("not_found"));
        AssertEqual(400, AiChecklistApi.StatusCode("knowledge_empty"));
        AssertEqual(502, AiChecklistApi.StatusCode("ai_unavailable"));
        AssertEqual(503, AiChecklistApi.StatusCode("search_unavailable"));
        AssertEqual(409, AiChecklistApi.StatusCode("state_conflict"));
        AssertTrue(AmveraAiChecklistSynthesisClient.TryReadContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", out var content) && content == "{}", "Должен читаться стандартный ответ Amvera.");
        AssertTrue(!AmveraAiChecklistSynthesisClient.TryReadContent("{\"choices\":[]}", out _), "Пустой choices должен обрабатываться без исключения.");
        var contextEvidence = Enumerable.Range(1, 5).Select(index => new AiChecklistEvidence($"S{index}", "Тема", "Документ", "Раздел", new string('я', 3_000), .8)).ToArray();
        var firstContextBatch = AiChecklistBatchPlanner.Build(contextEvidence)[0];
        var boundedContext = AmveraAiChecklistSynthesisClient.BuildContext(contextEvidence.Where(item => firstContextBatch.EvidenceIds.Contains(item.Id)).ToArray());
        AssertTrue(boundedContext.Length <= 9_000, "Контекст одного LLM-вызова должен быть не длиннее 9000 символов.");
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
        AssertTrue(queries.Count is > 3 and <= 5, "Планировщик должен создавать не более пяти запросов.");
        AssertTrue(queries.All(item => item.Query.Length <= 500), "Поисковые запросы не должны содержать всю карточку целиком.");
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
              {"section":"Общие вопросы","title":"Проверить программу ПЭК","reason":"Объект I категории","confidence":0.91,"citations":[{"sourceId":"S1","quote":"Проверить программу ПЭК"}]},
              {"section":"Общие вопросы","title":" Проверить программу ПЭК ","reason":"Дубликат","confidence":0.5,"citations":[{"sourceId":"S1","quote":"Проверить программу ПЭК"}]},
              {"section":"Отходы","title":"Неподтверждённый пункт","reason":"Нет источника","confidence":0.7,"citations":[{"sourceId":"S404","quote":"несуществующая цитата"}]}
            ]}
            """;
        var parsed = AiChecklistOutputParser.Parse(json, evidence);
        AssertEqual("ИИ-проверка", parsed.Name);
        AssertEqual(1, parsed.Items.Count);
        AssertEqual("S1", parsed.Items[0].SourceIds[0]);

        var invalid = AiChecklistOutputParser.Parse("{\"name\":\"x\",\"items\":[{\"title\":\"Без ссылки\",\"citations\":[]}]}", evidence);
        AssertEqual(0, invalid.Items.Count);
        var unrelated = AiChecklistOutputParser.Parse("{\"name\":\"x\",\"items\":[{\"title\":\"Оформить программу аренды автомобиля\",\"reason\":\"Транспорт\",\"citations\":[{\"sourceId\":\"S1\",\"quote\":\"Проверить программу ПЭК\"}]}]}", evidence);
        AssertEqual(0, unrelated.Items.Count);

        var wrapped = AiChecklistOutputParser.Parse($"<think>служебное рассуждение</think>\n```json\n{json}\n```", evidence);
        AssertEqual(1, wrapped.Items.Count);

        var invalidBeforeValid = AiChecklistOutputParser.Parse("""
            {"name":"x","items":[
              {"title":"Проверить ПЭК","citations":[{"sourceId":"S404","quote":"несуществующая цитата"}]},
              {"title":"Проверить ПЭК","citations":[{"sourceId":"S1","quote":"Проверить программу ПЭК"}]}
            ]}
            """, evidence);
        AssertEqual(1, invalidBeforeValid.Items.Count);

        var oversizedItems = string.Join(',', Enumerable.Range(1, 105).Select(index => $"{{\"title\":\"Программа ПЭК {index}\",\"citations\":[{{\"sourceId\":\"S1\",\"quote\":\"Проверить программу ПЭК\"}}]}}"));
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
        var runId = Guid.NewGuid();
        var idempotentRequest = new CreateAiChecklistDraftRequest(facilityId, "Березниковское ЛПУМГ", "ИИ-проверка",
            [new("ПЭК", "Проверить программу ПЭК", "ФЗ-7", "Источник S1")], runId);
        var first = await repository.CreateAiDraftAsync(idempotentRequest, CancellationToken.None);
        var second = await repository.CreateAiDraftAsync(idempotentRequest, CancellationToken.None);
        AssertEqual(first.Value!.Id, second.Value!.Id);
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
            {"name":"ИИ-проверка","items":[{"section":"ПЭК","title":"Проверить программу","reason":"I категория","confidence":0.9,"citations":[{"sourceId":"S1","quote":"Проверить программу ПЭК"}]}]}
            """), repository, new InMemoryAiChecklistRunStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AiChecklistAgent>.Instance);

        var generated = await agent.GenerateAsync("test", CancellationToken.None);
        AssertTrue(generated.IsSuccess, "Агент должен сохранять валидный результат поиска и синтеза.");
        AssertEqual("ai", generated.Value!.Items[0].Origin);
        AssertTrue(generated.Value.Items[0].Basis.Contains("ФЗ-7"), "Основание должно содержать найденный документ.");

        var emptyAgent = new AiChecklistAgent(source, new FakeSearch([]), new FakeSynthesis("{}"), repository, new InMemoryAiChecklistRunStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiChecklistAgent>.Instance);
        var rejected = await emptyAgent.GenerateAsync("test", CancellationToken.None);
        AssertEqual("knowledge_empty", rejected.ErrorCode);

        var runStore = new InMemoryAiChecklistRunStore();
        var countingSearch = new FakeSearch(evidence);
        var batchedAgent = new AiChecklistAgent(source, countingSearch, new FakeSynthesis("""
            {"name":"ПЭК","items":[{"section":"ПЭК","title":"Проверить программу ПЭК","confidence":0.9,"citations":[{"sourceId":"S1","quote":"Проверить программу ПЭК"}]}]}
            """), repository, runStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<AiChecklistAgent>.Instance);
        var createdRun = await batchedAgent.CreateRunAsync("test", CancellationToken.None);
        AssertTrue(createdRun.IsSuccess, "Поиск должен создать сохраняемый запуск.");
        await runStore.QueueBatchAsync(createdRun.Value!.Id, 0, CancellationToken.None);
        AssertTrue(await batchedAgent.ProcessNextBatchAsync(CancellationToken.None), "Worker должен обработать пакет из очереди.");
        var finalized = await batchedAgent.FinalizeRunAsync(createdRun.Value.Id, CancellationToken.None);
        AssertTrue(finalized.IsSuccess, "Завершённые пакеты должны создать черновик.");
        AssertEqual(1, countingSearch.Calls);
    }

    public static void RunBatchPlanningChecks()
    {
        var evidence = Enumerable.Range(1, 32).Select(index => new AiChecklistEvidence(
            $"S{index}", index <= 12 ? "Атмосфера" : "Отходы", $"Документ {index}", "Раздел", new string('x', 2_000), .8)).ToArray();
        var batches = AiChecklistBatchPlanner.Build(evidence);
        AssertTrue(batches.Count > 1, "Большой контекст должен делиться на пакеты.");
        AssertTrue(batches.All(batch => batch.EvidenceIds.Count <= 5), "В пакете должно быть не более пяти источников.");
        AssertTrue(batches.All(batch => batch.ContextCharacters <= 9_000), "Контекст пакета должен быть ограничен 9000 символами.");
        AssertEqual(32, batches.SelectMany(batch => batch.EvidenceIds).Distinct().Count());
        AssertEqual(0, AiChecklistBatchPlanner.Build([]).Count);

        var boundaryEvidence = new[]
        {
            new AiChecklistEvidence("S1", "Одна тема", "Документ 1", "Раздел", new string('а', 8_800) + " КОНЕЦ-S1", .9),
            new AiChecklistEvidence("S2", "Одна тема", "Документ 2", "Раздел", new string('б', 100) + " КОНЕЦ-S2", .8)
        };
        foreach (var batch in AiChecklistBatchPlanner.Build(boundaryEvidence))
        {
            var batchEvidence = boundaryEvidence.Where(item => batch.EvidenceIds.Contains(item.Id)).ToArray();
            var rendered = AmveraAiChecklistSynthesisClient.BuildContext(batchEvidence);
            AssertTrue(batchEvidence.All(item => rendered.Contains($"КОНЕЦ-{item.Id}")), "Планировщик не должен назначать в пакет источник, который затем обрежется.");
        }

        var longText = new string('я', 20_000) + " КОНЕЦ";
        var prepared = AiChecklistBatchPlanner.PrepareEvidence(
            [new AiChecklistEvidence("LONG", "Тема", "Документ", "Раздел", longText, .7)]);
        AssertTrue(prepared.Count > 1, "Длинный источник должен делиться на несколько сохраняемых фрагментов.");
        AssertEqual(longText, string.Concat(prepared.Select(item => item.Text)));
        AssertTrue(AiChecklistBatchPlanner.Build(prepared).All(batch => batch.ContextCharacters <= 9_000), "Каждый фрагмент длинного источника должен помещаться в контекст целиком.");
    }

    private sealed class FakeFacilitySource(FacilityProfile profile, OperationalFacility facility) : IAiChecklistFacilitySource
    {
        public Task<FacilityProfile?> GetProfileAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<FacilityProfile?>(profile);
        public Task<OperationalFacility?> GetFacilityAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<OperationalFacility?>(facility);
    }

    private sealed class FakeSearch(IReadOnlyList<AiChecklistEvidence> evidence) : IAiChecklistKnowledgeSearch
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AiChecklistEvidence>> SearchAsync(IReadOnlyList<AiChecklistSearchQuery> queries, CancellationToken cancellationToken) { Calls++; return Task.FromResult(evidence); }
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
