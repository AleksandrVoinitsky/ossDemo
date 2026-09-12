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
