internal sealed record CoverageGap(
    string RequirementId,
    IReadOnlyList<string> ClassifierCodes,
    string Basis,
    string Requirement,
    string Reason);

internal sealed record FacilityChecklistComposition(
    IReadOnlyList<ClassifierDecision> ClassifierDecisions,
    IReadOnlySet<string> IncludedCriterionCodes,
    IReadOnlyList<RequirementCatalogItem> SelectedRequirements,
    IReadOnlySet<string> SelectedRequirementIds,
    IReadOnlyList<ChecklistItemCatalogItem> Items,
    IReadOnlySet<string> CoveredRequirementIds,
    IReadOnlyList<CoverageGap> Gaps,
    bool CanFinalizeChecklist);

internal sealed class FacilityChecklistComposer(
    ClassifierTree tree,
    RequirementCatalog requirements,
    IReadOnlyList<ChecklistItemCatalogItem> items)
{
    public FacilityChecklistComposition Compose(
        FacilityProfileFields profile,
        IReadOnlyList<ChecklistHistoryReference>? history = null,
        string? scheduleCriteria = null)
    {
        var facts = FacilityFactNormalizer.Normalize(profile, scheduleCriteria);
        var decisions = ClassifierApplicabilityMatcher.Decide(tree, facts, history ?? []);
        var includedCodes = decisions.Where(item => item.Outcome == "included")
            .Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedRequirements = requirements.Items
            .Where(requirement => requirement.ClassifierCodes.Any(includedCodes.Contains))
            .Where(requirement => MatchesCategory(requirement.Categories, profile.Category))
            .ToArray();
        var selectedIds = selectedRequirements.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applicableItems = items
            .Where(item => item.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.ClassifierCodes.Any(includedCodes.Contains))
            .Where(item => item.RequirementIds.Count > 0 && item.RequirementIds.All(selectedIds.Contains))
            .ToArray();
        var historicalItems = applicableItems.Where(item => !item.Provenance.StartsWith("requirements-registry-direct", StringComparison.OrdinalIgnoreCase)).ToArray();
        var historicallyCovered = historicalItems.SelectMany(item => item.RequirementIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = historicalItems.Concat(applicableItems.Where(item => item.Provenance.StartsWith("requirements-registry-direct", StringComparison.OrdinalIgnoreCase)
                && item.RequirementIds.Any(requirementId => !historicallyCovered.Contains(requirementId))))
            .OrderBy(item => item.SectionCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Position)
            .ToArray();
        var coveredIds = selectedItems.SelectMany(item => item.RequirementIds)
            .Where(selectedIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaps = selectedRequirements.Where(item => !coveredIds.Contains(item.Id))
            .Select(item => new CoverageGap(item.Id, item.ClassifierCodes.Where(includedCodes.Contains).ToArray(), item.Basis, item.Requirement,
                "Для применимого требования нет утвержденного проверочного пункта."))
            .ToArray();
        var profileReady = profile.StructuredProfile is not null && FacilityProfileReadiness.Evaluate(profile.StructuredProfile).CanFinalizeChecklist;
        var canFinalize = profileReady
            && decisions.All(item => item.Outcome != "blocked_unknown")
            && gaps.Length == 0;
        return new(decisions, includedCodes, selectedRequirements, selectedIds, selectedItems, coveredIds, gaps, canFinalize);
    }

    private static bool MatchesCategory(IReadOnlyList<string> categories, string profileCategory)
    {
        if (categories.Any(value => value.Contains("без кат", StringComparison.OrdinalIgnoreCase))) return true;
        var constrained = categories.Where(value => !value.Contains("без кат", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (constrained.Length == 0 || string.IsNullOrWhiteSpace(profileCategory)) return true;
        return constrained.Any(category => ContainsWholeToken(profileCategory, category));
    }

    private static bool ContainsWholeToken(string actual, string expected)
    {
        var start = 0;
        while ((start = actual.IndexOf(expected, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = start == 0 || !char.IsLetterOrDigit(actual[start - 1]);
            var end = start + expected.Length;
            var after = end == actual.Length || !char.IsLetterOrDigit(actual[end]);
            if (before && after) return true;
            start++;
        }
        return false;
    }
}
