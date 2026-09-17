internal static class ClassifierChecks
{
    public static void Run()
    {
        AssertEqual(7, ClassifierSeedData.Sections.Count);
        AssertEqual(49, ClassifierSeedData.Sections.Sum(section => section.Criteria.Count));
        AssertTrue(
            ClassifierSeedData.Sections.SelectMany(section => section.Criteria)
                .Select(criterion => criterion.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 49,
            "Коды классификатора должны быть уникальны.");
        AssertTrue(ClassifierSeedData.Tree.Sections.SelectMany(section => section.Criteria)
            .All(criterion => criterion.CheckText.StartsWith("Проверить ", StringComparison.OrdinalIgnoreCase) && criterion.CheckText.Length <= 220),
            "Каждый критерий должен иметь короткую рабочую формулировку.");

        var invalid = ClassifierRules.Normalize(new ClassifierCriterionWrite("", "", "", [], [], true, 0));
        AssertTrue(!invalid.IsSuccess, "Пустой критерий должен отклоняться.");

        var facts = FacilityFactNormalizer.Normalize(new FacilityProfileFields
        {
            TreatmentFacilities = "Да (локальные очистные сооружения)",
            Zones = "КОС/ЛОС",
            EnvironmentalAspects = "Сбросы в водные объекты"
        }, null);
        var selected = ClassifierApplicabilityMatcher.Match(ClassifierSeedData.Tree, facts, []);
        AssertTrue(selected.Any(item => item.Criterion.Code == "3.9"), "Очистные сооружения должны включать критерий 3.9.");
        AssertTrue(selected.All(item => !string.IsNullOrWhiteSpace(item.Reason)), "Причина применимости обязательна.");
        AssertTrue(selected.Any(item => item.Criterion.Code == "1.6"), "Базовые критерии должны включаться всегда.");
        AssertTrue(!selected.Any(item => item.Criterion.Code == "2.4"), "Критерии атмосферы не должны включаться без признаков выбросов.");

        var water = selected.Single(item => item.Criterion.Code == "3.9");
        var query = AiChecklistQueryPlanner.Build(water, facts);
        AssertTrue(query.Key == "3.9" && query.Query.Contains("КОС", StringComparison.OrdinalIgnoreCase), "Запрос должен связывать критерий с совпавшим фактом карточки.");
        AssertTrue(query.Label.Contains(water.Criterion.CheckText, StringComparison.Ordinal), "Модель должна получить формулировку выбранного критерия.");
        AssertTrue(selected.Select(item => AiChecklistQueryPlanner.Build(item, facts)).All(item => item.Query.Length <= 280),
            "Поиск по критерию должен получать короткий запрос без полной формулировки риска.");

        var thirdCategory = FacilityFactNormalizer.Normalize(new FacilityProfileFields
        {
            Category = "III категория"
        }, null);
        var thirdCategorySelection = ClassifierApplicabilityMatcher.Match(ClassifierSeedData.Tree, thirdCategory, []);
        AssertTrue(!thirdCategorySelection.Any(item => item.Criterion.Code == "1.2"),
            "Римская I не должна совпадать подстрокой с III категорией.");

        var secondCategory = FacilityFactNormalizer.Normalize(new FacilityProfileFields
        {
            Category = "II категория"
        }, null);
        var secondCategorySelection = ClassifierApplicabilityMatcher.Match(ClassifierSeedData.Tree, secondCategory, []);
        AssertTrue(secondCategorySelection.Any(item => item.Criterion.Code == "1.2"),
            "II категория должна включать критерий комплексного экологического разрешения.");

        var mappingRows = ClassifierMappingCatalog.ParseLines([
            "{\"code\":\"1.2\",\"section\":\"1. Общие вопросы\",\"name\":\"КЭР\",\"categories\":[\"I\"],\"types\":[\"Компрессорная станция\"],\"zones\":[],\"equipment\":[],\"specialZones\":[],\"environmentalAspects\":[],\"regions\":[\"Пермский край\"]}",
            "{\"code\":\"1.3\",\"section\":\"1. Общие вопросы\",\"name\":\"Плата за НВОС\",\"categories\":[\"I\",\"II\",\"III\",\"IV\"],\"types\":[\"Компрессорная станция\"],\"zones\":[],\"equipment\":[],\"specialZones\":[],\"environmentalAspects\":[],\"regions\":[\"Пермский край\"]}"
        ]);
        var mappedTree = ClassifierMappingCatalog.Apply(ClassifierSeedData.Tree, mappingRows);
        var mappedThirdCategory = FacilityFactNormalizer.Normalize(new FacilityProfileFields
        {
            Category = "III категория",
            Type = "Компрессорная станция",
            Region = "Пермский край"
        }, null);
        var mappedThirdSelection = ClassifierApplicabilityMatcher.Match(mappedTree, mappedThirdCategory, []);
        AssertTrue(!mappedThirdSelection.Any(item => item.Criterion.Code == "1.2"),
            "Обязательное ограничение mapping по категории должно исключать критерий 1.2 для категории III.");
        AssertTrue(mappedThirdSelection.Any(item => item.Criterion.Code == "1.3"),
            "Критерий с подходящими категорией, регионом и типом должен применяться.");

        var partialStructured = StructuredProfile("partial", FacilityFactState.Absent);
        partialStructured.Features["air.emissions"] = new(FacilityFactState.Present, "Стационарные источники");
        var partialDecisions = ClassifierApplicabilityMatcher.Decide(
            mappedTree,
            FacilityFactNormalizer.Normalize(new FacilityProfileFields
            {
                Category = "III категория",
                Region = "Пермский край",
                StructuredProfile = partialStructured
            }, null), []);
        AssertEqual("included", partialDecisions.Single(item => item.Code == "2.2").Outcome);
        AssertTrue(partialDecisions.All(item => item.Reason != "Не заполнено идентификационное поле «type»."),
            "Отсутствующий необязательный тип не должен блокировать подбор по известным фактам.");

        var approvedMappingPath = Path.Combine(AppContext.BaseDirectory, "requirements", "classifier-mapping.jsonl");
        AssertTrue(File.Exists(approvedMappingPath), "Утверждённый mapping должен входить в результат сборки.");
        var approvedMapping = ClassifierMappingCatalog.ParseLines(File.ReadLines(approvedMappingPath));
        AssertEqual(49, approvedMapping.Count);
        AssertEqual(49, approvedMapping.Select(row => row.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var detailedTree = ClassifierMappingCatalog.Apply(ClassifierSeedData.Tree, approvedMapping);
        var emissionsWithoutGasCleaning = FacilityFactNormalizer.Normalize(new FacilityProfileFields
        {
            Category = "III категория", Region = "Пермский край", Type = "Компрессорная станция",
            EnvironmentalAspects = "Выбросы в атмосферный воздух", Equipment = "Котельная установка"
        }, null);
        var withoutGasCleaning = ClassifierApplicabilityMatcher.Decide(detailedTree, emissionsWithoutGasCleaning, []);
        AssertEqual("excluded", withoutGasCleaning.Single(item => item.Code == "2.4").Outcome);

        var gasCleaning = FacilityFactNormalizer.Normalize(new FacilityProfileFields
        {
            Category = "III категория", Region = "Пермский край", Type = "Компрессорная станция",
            EnvironmentalAspects = "Выбросы в атмосферный воздух", Equipment = "Установка очистки газа"
        }, null);
        var withGasCleaning = ClassifierApplicabilityMatcher.Decide(detailedTree, gasCleaning, []);
        AssertEqual("included", withGasCleaning.Single(item => item.Code == "2.4").Outcome);
        AssertTrue(withGasCleaning.Single(item => item.Code == "2.4").Reason.Contains("equipment", StringComparison.OrdinalIgnoreCase),
            "Причина должна ссылаться на конкретное поле mapping, которое включило критерий.");

        var industrialProfile = StructuredProfile("industrial", FacilityFactState.Absent);
        industrialProfile.Features["air.emissions"] = new(FacilityFactState.Present, "Два стационарных источника");
        var industrialDecisions = ClassifierApplicabilityMatcher.Decide(
            ClassifierSeedData.Tree,
            FacilityFactNormalizer.Normalize(new FacilityProfileFields { Type = "Компрессорная станция", Category = "III категория", Region = "Пермский край", StructuredProfile = industrialProfile }, null), []);
        AssertEqual("included", industrialDecisions.Single(item => item.Code == "2.2").Outcome);
        AssertTrue(industrialDecisions.Single(item => item.Code == "2.2").Facts.Any(item => item.Code == "air.emissions" && item.State == FacilityFactState.Present),
            "Решение должно содержать факт, который включил критерий.");

        var officeProfile = StructuredProfile("office", FacilityFactState.Absent);
        var officeDecisions = ClassifierApplicabilityMatcher.Decide(
            ClassifierSeedData.Tree,
            FacilityFactNormalizer.Normalize(new FacilityProfileFields { Type = "Административное здание", Category = "IV категория", Region = "Пермский край", StructuredProfile = officeProfile }, null),
            [new("2.2", "Историческая проверка выбросов", true)]);
        AssertEqual("excluded", officeDecisions.Single(item => item.Code == "2.2").Outcome);
        AssertTrue(!ClassifierApplicabilityMatcher.Match(officeDecisions,
                FacilityFactNormalizer.Normalize(new FacilityProfileFields { StructuredProfile = officeProfile }, null))
            .Any(item => item.Criterion.Code == "2.2"), "История не должна включать критерий при подтвержденном отсутствии признака.");

        var unknownProfile = StructuredProfile("unknown", FacilityFactState.Absent);
        unknownProfile.Features["air.emissions"] = new(FacilityFactState.Unknown);
        var unknownDecisions = ClassifierApplicabilityMatcher.Decide(
            ClassifierSeedData.Tree,
            FacilityFactNormalizer.Normalize(new FacilityProfileFields { Type = "Компрессорная станция", Category = "III категория", Region = "Пермский край", StructuredProfile = unknownProfile }, null), []);
        AssertEqual("blocked_unknown", unknownDecisions.Single(item => item.Code == "2.2").Outcome);
        AssertEqual(49, unknownDecisions.Count);
    }

    private static FacilityProfileV2 StructuredProfile(string slug, FacilityFactState state)
    {
        var profile = FacilityProfileV2.CreateEmpty(slug, slug);
        foreach (var code in FacilityProfileV2.RequiredFeatureCodes) profile.Features[code] = new(state);
        profile.VerificationStatus = "verified";
        return profile;
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Ожидалось: {expected}; получено: {actual}.");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
