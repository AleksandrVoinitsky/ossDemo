using System.Text.RegularExpressions;

internal static partial class FacilityRequirementSelector
{
    private static readonly IReadOnlyDictionary<string, string> RegionCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Пермский край"] = "59",
            ["Удмуртская Республика"] = "18",
            ["Кировская область"] = "43"
        };

    public static IReadOnlyList<RequirementCatalogItem> Select(
        RequirementCatalog catalog,
        IReadOnlySet<string> criterionCodes,
        FacilityProfileFields profile,
        IReadOnlySet<string>? allowedRequirementIds = null)
    {
        var category = CategoryCode(profile.Category);
        var regionCode = RegionCodes
            .FirstOrDefault(item => profile.Region.Contains(item.Key, StringComparison.OrdinalIgnoreCase)).Value;

        return catalog.Items
            .Where(item => item.ClassifierCodes.Any(criterionCodes.Contains))
            .Where(item => allowedRequirementIds is null || allowedRequirementIds.Contains(item.Id))
            .Where(item => AppliesToFacility(item, category, regionCode))
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AppliesToFacility(RequirementCatalogItem item, string category, string? regionCode)
    {
        if (item.Categories.Count == 0 || item.Categories.Any(value => value.Equals("Без кат.", StringComparison.OrdinalIgnoreCase)))
            return true;

        var regional = item.Levels.Any(value => value.Contains("Региональ", StringComparison.OrdinalIgnoreCase));
        if (regional)
            return !string.IsNullOrWhiteSpace(regionCode)
                && item.Categories.Any(value => value.Equals(regionCode, StringComparison.OrdinalIgnoreCase));

        var categoryValues = item.Categories.Select(CategoryCode).Where(value => value.Length > 0).ToArray();
        return categoryValues.Length == 0 || categoryValues.Contains(category, StringComparer.OrdinalIgnoreCase);
    }

    private static string CategoryCode(string? value)
    {
        var match = CategoryRegex().Match(value ?? string.Empty);
        return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
    }

    [GeneratedRegex(@"(?<![A-ZА-Я])(IV|III|II|I)(?![A-ZА-Я])", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryRegex();
}
