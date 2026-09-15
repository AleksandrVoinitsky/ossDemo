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
    RequirementCatalog requirements,
    IReadOnlyList<InspectionControl> controls)
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
        var includedSections = includedCodes.Select(code => code.Split('.')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = controls
            .Where(control => includedSections.Contains(control.SectionCode))
            .Select(control => control with
            {
                ClassifierCodes = decisions
                    .Where(decision => decision.Outcome == "included"
                        && decision.Code.StartsWith(control.SectionCode + ".", StringComparison.OrdinalIgnoreCase))
                    .Select(decision => decision.Code)
                    .ToArray(),
                RequirementIds = allowedRequirementIds is null
                    ? control.RequirementIds
                    : control.RequirementIds.Where(allowedRequirementIds.Contains).ToArray()
            })
            .Where(control => allowedRequirementIds is null
                || control.RequirementIds.Count > 0
                || control.LinkStatus == "unmapped")
            .OrderBy(item => item.Position)
            .ToArray();
        var selectedIds = allowedRequirementIds is null
            ? selectedItems.SelectMany(item => item.RequirementIds).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : requirements.Items.Where(item => allowedRequirementIds.Contains(item.Id)).Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedRequirements = requirements.Items.Where(item => selectedIds.Contains(item.Id)).ToArray();
        var coveredIds = selectedItems.SelectMany(item => item.RequirementIds)
            .Where(selectedIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaps = allowedRequirementIds is null
            ? Array.Empty<CoverageGap>()
            : selectedRequirements.Where(item => !coveredIds.Contains(item.Id))
                .Select(item => new CoverageGap(item.Id, item.ClassifierCodes, item.Basis, item.Requirement,
                    "Для применимого требования пока не подтверждена проверочная процедура."))
                .ToArray();
        var canFinalize = selectedItems.Length > 0;
        return new(decisions, includedCodes, selectedRequirements, selectedIds, selectedItems, coveredIds, gaps, canFinalize);
    }
}
