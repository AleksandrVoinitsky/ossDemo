internal static class ChecklistItemCatalogChecks
{
    public static void Run()
    {
        var requirementPath = Path.Combine(AppContext.BaseDirectory, "requirements", "requirements-registry.jsonl");
        var itemPath = Path.Combine(AppContext.BaseDirectory, "requirements", "checklist-item-catalog.jsonl");
        var requirements = RequirementCatalog.Load(requirementPath);
        var approved = ChecklistItemCatalog.Load(itemPath).Where(item => item.Status == "approved").ToArray();

        AssertTrue(requirements.Count > 3_000, "Реестр требований должен входить в сборку.");
        AssertTrue(approved.Length > 0, "Каталог должен содержать проверенные детерминированные связи.");
        AssertTrue(approved.All(item => item.ClassifierCodes.Count > 0), "Каждый пункт должен иметь код классификатора.");
        AssertTrue(approved.All(item => item.RequirementIds.Count > 0), "Каждый пункт должен иметь требования.");
        AssertTrue(approved.SelectMany(item => item.RequirementIds).All(requirements.ContainsId), "Связь указывает на отсутствующее требование.");
        AssertTrue(approved.All(item => !string.IsNullOrWhiteSpace(item.LinkExplanation)), "Для связи требуется объяснение.");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
