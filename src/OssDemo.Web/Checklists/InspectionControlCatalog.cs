internal sealed record InspectionControl(
    string Id,
    int Position,
    string SectionCode,
    string Section,
    string Title,
    string Basis,
    IReadOnlyList<string> ClassifierCodes,
    IReadOnlyList<string> RequirementIds,
    string LinkStatus,
    string Provenance,
    string LinkExplanation);

internal static class InspectionControlCatalog
{
    public static IReadOnlyList<InspectionControl> Build(
        IReadOnlyList<InspectorChecklistTemplateItem> template,
        IReadOnlyList<ChecklistItemCatalogItem> linkedItems)
    {
        var verified = linkedItems
            .Where(item => !item.Provenance.StartsWith("requirements-registry-direct", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        return template.Select(item => verified.TryGetValue(item.Id, out var link)
                ? new InspectionControl(
                    item.Id, item.Position, item.SectionCode, item.Section, item.Title, item.Basis,
                    link.ClassifierCodes, link.RequirementIds, "verified", link.Provenance, link.LinkExplanation)
                : new InspectionControl(
                    item.Id, item.Position, item.SectionCode, item.Section, item.Title, item.Basis,
                    [], [], "unmapped", "inspector-template-v1",
                    "Утверждённая контрольная процедура; точная связь со строкой реестра требует подтверждения."))
            .OrderBy(item => item.Position)
            .ToArray();
    }
}
