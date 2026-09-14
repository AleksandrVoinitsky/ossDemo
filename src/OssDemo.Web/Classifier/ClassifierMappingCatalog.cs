using System.Text.Json;

internal sealed record ClassifierMappingRow(
    string Code,
    string Section,
    string Name,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Equipment,
    IReadOnlyList<string> SpecialZones,
    IReadOnlyList<string> EnvironmentalAspects,
    IReadOnlyList<string> Regions);

internal static class ClassifierMappingCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ClassifierMappingRow> ParseLines(IEnumerable<string> lines) => lines
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<ClassifierMappingRow>(line, JsonOptions)
            ?? throw new InvalidDataException("Строка mapping не содержит данных."))
        .ToArray();

    public static ClassifierTree Apply(ClassifierTree tree, IReadOnlyList<ClassifierMappingRow> rows)
    {
        var byCode = rows.ToDictionary(row => row.Code, StringComparer.OrdinalIgnoreCase);
        return tree with
        {
            Sections = tree.Sections.Select(section => section with
            {
                Criteria = section.Criteria.Select(criterion => byCode.TryGetValue(criterion.Code, out var row)
                    ? criterion with { ApplicabilityRules = BuildRules(row) }
                    : criterion).ToArray()
            }).ToArray()
        };
    }

    public static ClassifierTree ApplyFile(ClassifierTree tree, string path) =>
        Apply(tree, ParseLines(File.ReadLines(path)));

    private static IReadOnlyList<ClassifierApplicabilityRule> BuildRules(ClassifierMappingRow row)
    {
        var rules = new List<ClassifierApplicabilityRule>();
        Add("category", "required-any", row.Categories);
        Add("region", "required-any", row.Regions);
        Add("type", "contains-any", row.Types);
        Add("zones", "contains-any", row.Zones);
        Add("equipment", "contains-any", row.Equipment);
        Add("specialZones", "contains-any", row.SpecialZones);
        Add("environmentalAspects", "contains-any", row.EnvironmentalAspects);
        return rules;

        void Add(string field, string operation, IReadOnlyList<string> values)
        {
            if (values.Count > 0) rules.Add(new(field, operation, values));
        }
    }
}
