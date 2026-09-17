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
    IReadOnlyList<InspectionControl> Items,
    IReadOnlySet<string> CoveredRequirementIds,
    IReadOnlyList<CoverageGap> Gaps,
    bool CanFinalizeChecklist);

internal sealed class FacilityChecklistComposer(
    ClassifierTree tree,
    RequirementCatalog requirements)
{
    public FacilityChecklistComposition Compose(
        FacilityProfileFields profile,
        IReadOnlyList<ChecklistHistoryReference>? history = null,
        string? scheduleCriteria = null,
        IReadOnlySet<string>? allowedRequirementIds = null)
    {
        var facts = FacilityFactNormalizer.Normalize(profile, scheduleCriteria);
        var decisions = ClassifierApplicabilityMatcher.Decide(tree, facts, history ?? []);
        var includedCodes = decisions.Where(item => item.Outcome == "included")
            .Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedRequirements = FacilityRequirementSelector.Select(requirements, includedCodes, profile, allowedRequirementIds);
        var selectedIds = selectedRequirements.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = RequirementControlCatalog.Build(tree, selectedRequirements, includedCodes);
        var coveredIds = selectedItems.SelectMany(item => item.RequirementIds)
            .Where(selectedIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaps = Array.Empty<CoverageGap>();
        var canFinalize = selectedItems.Count > 0;
        return new(decisions, includedCodes, selectedRequirements, selectedIds, selectedItems, coveredIds, gaps, canFinalize);
    }
}
