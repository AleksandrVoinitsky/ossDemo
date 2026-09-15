internal sealed record RequirementLinkOverride(string RequirementId, string Action, long Version);

internal sealed record RequirementRevision(
    string RequirementId,
    string Requirement,
    string Basis,
    long Version);

internal sealed record ResolvedRequirement(
    string Id,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> ClassifierCodes,
    string Basis,
    string Requirement,
    IReadOnlyList<string> Categories,
    string LinkSource,
    bool IsRevised,
    long Version);

internal sealed record ResolvedCriterionRequirements(
    string CriterionCode,
    IReadOnlyList<ResolvedRequirement> Items,
    IReadOnlyList<ResolvedRequirement> ExcludedItems);

internal sealed record RequirementRevisionWrite(string? Requirement, string? Basis, long Version);
internal sealed record RequirementLinkWrite(string? Action);

internal static class RequirementWorkspaceRules
{
    public static ChecklistOperationResult<RequirementRevisionWrite> NormalizeRevision(RequirementRevisionWrite request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Requirement)) errors["requirement"] = ["Укажите формулировку требования."];
        if (string.IsNullOrWhiteSpace(request.Basis)) errors["basis"] = ["Укажите нормативное основание."];
        if (request.Version < 1) errors["version"] = ["Версия требования некорректна."];
        return errors.Count > 0
            ? ChecklistOperationResult<RequirementRevisionWrite>.Fail("validation", "Проверьте поля требования.", errors)
            : ChecklistOperationResult<RequirementRevisionWrite>.Success(request with
            {
                Requirement = request.Requirement!.Trim(),
                Basis = request.Basis!.Trim()
            });
    }

    public static string? NormalizeLinkAction(string? action)
    {
        var normalized = action?.Trim().ToLowerInvariant();
        return normalized is "include" or "exclude" ? normalized : null;
    }
}

internal static class RequirementWorkspaceResolver
{
    public static ResolvedCriterionRequirements Resolve(
        IReadOnlyList<RequirementCatalogItem> sourceItems,
        string criterionCode,
        IReadOnlyList<RequirementLinkOverride> linkOverrides,
        IReadOnlyList<RequirementRevision> revisions)
    {
        var sources = sourceItems.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var selected = sourceItems
            .Where(item => item.ClassifierCodes.Contains(criterionCode, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.Id, _ => "automatic", StringComparer.OrdinalIgnoreCase);
        var excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in linkOverrides
                     .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.MaxBy(item => item.Version)!))
        {
            if (link.Action.Equals("exclude", StringComparison.OrdinalIgnoreCase))
            {
                selected.Remove(link.RequirementId);
                if (sources.ContainsKey(link.RequirementId)) excluded[link.RequirementId] = "excluded";
            }
            else if (link.Action.Equals("include", StringComparison.OrdinalIgnoreCase) && sources.ContainsKey(link.RequirementId))
            {
                selected[link.RequirementId] = "manual";
                excluded.Remove(link.RequirementId);
            }
        }

        var latestRevisions = revisions
            .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.MaxBy(item => item.Version)!, StringComparer.OrdinalIgnoreCase);

        ResolvedRequirement ResolveItem(KeyValuePair<string, string> pair)
            {
                var source = sources[pair.Key];
                latestRevisions.TryGetValue(source.Id, out var revision);
                return new ResolvedRequirement(
                    source.Id,
                    source.Levels,
                    source.Groups,
                    source.ClassifierCodes,
                    revision?.Basis ?? source.Basis,
                    revision?.Requirement ?? source.Requirement,
                    source.Categories,
                    pair.Value,
                    revision is not null,
                    revision?.Version ?? 1);
            }

        var items = selected.Select(ResolveItem)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedItems = excluded.Select(ResolveItem)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ResolvedCriterionRequirements(criterionCode, items, excludedItems);
    }
}
