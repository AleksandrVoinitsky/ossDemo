internal static class ChecklistItemCatalogChecks
{
    public static void Run()
    {
        var requirementPath = Path.Combine(AppContext.BaseDirectory, "requirements", "requirements-registry.jsonl");
        var itemPath = Path.Combine(AppContext.BaseDirectory, "requirements", "checklist-item-catalog.jsonl");
        var templatePath = Path.Combine(AppContext.BaseDirectory, "requirements", "inspector-checklist-template.jsonl");
        var requirements = RequirementCatalog.Load(requirementPath);
        var approved = ChecklistItemCatalog.Load(itemPath).Where(item => item.Status == "approved").ToArray();
        var template = InspectorChecklistTemplate.ParseLines(File.ReadLines(templatePath));
        var controls = InspectionControlCatalog.Build(template, approved);

        AssertTrue(requirements.Count > 3_000, "Реестр требований должен входить в сборку.");
        AssertTrue(approved.Length > 0, "Каталог должен содержать проверенные детерминированные связи.");
        AssertTrue(approved.All(item => item.ClassifierCodes.Count > 0), "Каждый пункт должен иметь код классификатора.");
        AssertTrue(approved.All(item => item.RequirementIds.Count > 0), "Каждый пункт должен иметь требования.");
        AssertTrue(approved.SelectMany(item => item.RequirementIds).All(requirements.ContainsId), "Связь указывает на отсутствующее требование.");
        AssertTrue(approved.All(item => !string.IsNullOrWhiteSpace(item.LinkExplanation)), "Для связи требуется объяснение.");
        AssertEqual(235, controls.Count);
        AssertEqual(34, controls.Count(item => item.LinkStatus == "verified"));
        AssertEqual(201, controls.Count(item => item.LinkStatus == "unmapped"));
        AssertTrue(controls.All(item => !string.IsNullOrWhiteSpace(item.Basis)),
            "Каждая процедура должна сохранять нормативное основание.");
        AssertTrue(controls.All(item => !item.Provenance.StartsWith("requirements-registry-direct", StringComparison.OrdinalIgnoreCase)),
            "Прямые строки реестра не являются контрольными процедурами.");
    }

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
