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
