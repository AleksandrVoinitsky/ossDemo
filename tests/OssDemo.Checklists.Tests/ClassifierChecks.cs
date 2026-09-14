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

        var approvedMappingPath = Path.Combine(AppContext.BaseDirectory, "requirements", "classifier-mapping.jsonl");
        AssertTrue(File.Exists(approvedMappingPath), "Утверждённый mapping должен входить в результат сборки.");
        var approvedMapping = ClassifierMappingCatalog.ParseLines(File.ReadLines(approvedMappingPath));
        AssertEqual(49, approvedMapping.Count);
        AssertEqual(49, approvedMapping.Select(row => row.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
