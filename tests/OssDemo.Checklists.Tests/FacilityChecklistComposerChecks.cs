internal static class FacilityChecklistComposerChecks
{
    public static void Run()
    {
        var requirements = RequirementCatalog.Load(Path.Combine(AppContext.BaseDirectory, "requirements", "requirements-registry.jsonl"));
        var items = ChecklistItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "requirements", "checklist-item-catalog.jsonl"));
        var composer = new FacilityChecklistComposer(ClassifierSeedData.Tree, requirements, items);

        var industrial = Profile("industrial", FacilityFactState.Absent);
        industrial.StructuredProfile!.Features["air.emissions"] = new(FacilityFactState.Present, "Стационарные источники");
        var office = Profile("office", FacilityFactState.Absent);
        var industrialResult = composer.Compose(industrial);
        var officeResult = composer.Compose(office);

        AssertTrue(!industrialResult.Items.Select(item => item.Id).SequenceEqual(officeResult.Items.Select(item => item.Id)),
            "Контрастные карточки должны формировать разные наборы проверок.");
        AssertTrue(industrialResult.Items.All(item => item.ClassifierCodes.Any(industrialResult.IncludedCriterionCodes.Contains)),
            "Пункт должен относиться к включенному критерию.");
        AssertTrue(industrialResult.Items.SelectMany(item => item.RequirementIds).All(industrialResult.SelectedRequirementIds.Contains),
            "Пункт не должен ссылаться на неприменимое требование.");
        AssertEqual(industrialResult.SelectedRequirementIds.Count,
            industrialResult.CoveredRequirementIds.Concat(industrialResult.Gaps.Select(gap => gap.RequirementId)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        AssertEqual(0, industrialResult.Gaps.Count);
        AssertTrue(industrialResult.CanFinalizeChecklist, "Прямые пункты реестра должны закрывать нормативное покрытие без ИИ.");

        industrial.StructuredProfile!.Features["water.discharge"] = new(FacilityFactState.Unknown);
        var blocked = composer.Compose(industrial);
        AssertTrue(blocked.CanFinalizeChecklist && blocked.ClassifierDecisions.Any(item => item.Outcome == "blocked_unknown"),
            "Неизвестный факт должен быть виден в трассировке, но не блокировать чек-лист по известным данным.");
    }

    private static FacilityProfileFields Profile(string slug, FacilityFactState defaultState)
    {
        var structured = FacilityProfileV2.CreateEmpty(slug, slug);
        foreach (var code in FacilityProfileV2.RequiredFeatureCodes) structured.Features[code] = new(defaultState);
        structured.VerificationStatus = "verified";
        return new() { FullName = slug, ShortName = slug, Category = "III категория", Region = "Пермский край", Type = "Компрессорная станция", StructuredProfile = structured };
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
