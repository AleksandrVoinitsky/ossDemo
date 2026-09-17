internal static class FacilityChecklistComposerChecks
{
    public static void Run()
    {
        var requirements = RequirementCatalog.Load(Path.Combine(AppContext.BaseDirectory, "requirements", "requirements-registry.jsonl"));
        var mapping = ClassifierMappingCatalog.ParseLines(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "requirements", "classifier-mapping.jsonl")));
        var tree = ClassifierMappingCatalog.Apply(ClassifierSeedData.Tree, mapping);
        var composer = new FacilityChecklistComposer(tree, requirements);

        var industrial = Profile("industrial");
        industrial.EnvironmentalAspects = "Выбросы в атмосферный воздух";
        industrial.Zones = "Компрессорный цех";
        industrial.Equipment = "Газоперекачивающий агрегат";
        var office = Profile("office");
        office.Type = "Административное здание";
        office.Category = "IV категория";
        office.EnvironmentalAspects = "Потребление природных ресурсов";
        var industrialResult = composer.Compose(industrial);
        var officeResult = composer.Compose(office);

        AssertTrue(!industrialResult.Items.Select(item => item.Id).SequenceEqual(officeResult.Items.Select(item => item.Id)),
            "Контрастные карточки должны формировать разные наборы проверок.");
        AssertTrue(industrialResult.Items.All(item => item.ClassifierCodes.Any(industrialResult.IncludedCriterionCodes.Contains)),
            "Пункт должен относиться к включенному критерию.");
        AssertEqual(0, industrialResult.Gaps.Count);
        AssertTrue(industrialResult.CanFinalizeChecklist, "Найденные требования должны формировать проверочные группы без ИИ.");
        AssertTrue(industrialResult.Items.All(item => item.Provenance == "requirements-registry-v2"),
            "Итоговые пункты должны строиться только из общего реестра требований.");
        AssertTrue(industrialResult.Items.Count < industrialResult.SelectedRequirements.Count,
            "Близкие требования должны объединяться в контрольные группы.");
        AssertTrue(industrialResult.Items.All(item => item.RequirementIds.Count is > 0 and <= 8),
            "Одна контрольная группа должна содержать от одного до восьми требований.");

        var oneRequirementId = industrialResult.SelectedRequirementIds.First();
        var narrowed = composer.Compose(industrial, allowedRequirementIds: new HashSet<string>([oneRequirementId], StringComparer.OrdinalIgnoreCase));
        AssertEqual(1, narrowed.SelectedRequirements.Count);
        AssertTrue(narrowed.Items.SelectMany(item => item.RequirementIds).All(id => id.Equals(oneRequirementId, StringComparison.OrdinalIgnoreCase)),
            "Исключённые разметкой требования не должны оставаться в итоговых пунктах.");

        var berezniki = Profile("bereznikovskoe");
        berezniki.Category = "I категория";
        berezniki.Zones = "Компрессорный цех; Газораспределительный пункт; Котельная; Очистные сооружения канализационные; Дизельная электростанция; Склад ГСМ; Артезианская скважина";
        berezniki.EnvironmentalAspects = "Выбросы в атмосферный воздух; Сбросы в водные объекты; Образование и обращение с отходами; Потребление природных ресурсов; Воздействие на земельные ресурсы и почвы; Аварийные и залповые воздействия";
        berezniki.Equipment = "Газоперекачивающий агрегат; Установка очистки газа; Артезианская скважина; Дизельная электростанция";
        berezniki.SpecialZones = "Водоохранная зона";
        var bereznikiResult = composer.Compose(berezniki);
        AssertTrue(bereznikiResult.Items.Count is >= 200 and <= 400,
            "Сложный объект должен давать проверяемый чек-лист порядка исторического референса.");
        AssertTrue(bereznikiResult.SelectedRequirements.All(item =>
                item.Categories.Contains("Без кат.", StringComparer.OrdinalIgnoreCase)
                || item.Categories.Contains("I", StringComparer.OrdinalIgnoreCase)
                || item.Categories.Contains("59", StringComparer.OrdinalIgnoreCase)),
            "Требования другой категории НВОС или другого региона не должны попадать в результат.");
    }

    private static FacilityProfileFields Profile(string slug) => new()
    {
        FullName = slug,
        ShortName = slug,
        Category = "III категория",
        Region = "Пермский край",
        Type = "Компрессорная станция"
    };

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
