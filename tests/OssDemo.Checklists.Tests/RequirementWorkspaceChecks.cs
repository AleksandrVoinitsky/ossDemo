internal static class RequirementWorkspaceChecks
{
    public static void Run()
    {
        var source = new RequirementCatalogItem(
            "R1", ["Федеральный"], ["Воздух"], ["2.1"], "ФЗ-7", "Исходная формулировка", ["Воздух"]);

        var revised = RequirementWorkspaceResolver.Resolve(
            [source], "2.1", [],
            [new RequirementRevision("R1", "Рабочая формулировка", "ФЗ-7, статья 1", 2)]);
        AssertEqual(1, revised.Items.Count);
        AssertEqual("Рабочая формулировка", revised.Items[0].Requirement);
        AssertEqual("automatic", revised.Items[0].LinkSource);
        AssertEqual(2L, revised.Items[0].Version);

        var excluded = RequirementWorkspaceResolver.Resolve(
            [source], "2.1",
            [new RequirementLinkOverride("R1", "exclude", 1)], []);
        AssertEqual(0, excluded.Items.Count);

        var manuallyIncluded = RequirementWorkspaceResolver.Resolve(
            [source], "3.1",
            [new RequirementLinkOverride("R1", "include", 1)], []);
        AssertEqual(1, manuallyIncluded.Items.Count);
        AssertEqual("manual", manuallyIncluded.Items[0].LinkSource);

        var invalidRevision = RequirementWorkspaceRules.NormalizeRevision(new RequirementRevisionWrite(" ", " ", 1));
        AssertEqual(false, invalidRevision.IsSuccess);
        var validRevision = RequirementWorkspaceRules.NormalizeRevision(new RequirementRevisionWrite("  Новый текст  ", "  Статья 2  ", 3));
        AssertEqual("Новый текст", validRevision.Value!.Requirement);
        AssertEqual("Статья 2", validRevision.Value.Basis);
        AssertEqual(3L, validRevision.Value.Version);
        AssertEqual("include", RequirementWorkspaceRules.NormalizeLinkAction(" INCLUDE "));
        AssertEqual<string?>(null, RequirementWorkspaceRules.NormalizeLinkAction("remove"));
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Ожидалось: {expected}; получено: {actual}.");
    }
}
