internal static partial class ClassifierSeedData
{
    public static readonly IReadOnlyList<ClassifierSeedSection> Sections = BuildSections();

    public static ClassifierTree Tree => new(
        new Guid("31000000-0000-0000-0000-000000000001"),
        "2026.01",
        "active",
        new DateOnly(2026, 1, 1),
        Sections.Select((section, sectionIndex) => new ClassifierSection(
            StableId($"section:{section.Code}"), section.Code, section.Title, sectionIndex,
            section.Criteria.Select((criterion, criterionIndex) => new ClassifierCriterion(
                StableId($"criterion:{criterion.Code}"), criterion.Code, criterion.RiskText,
                DefaultCheckText(criterion.RiskText), criterion.RiskText, DefaultRules(section.Code, criterion.Code), [],
                section.Code == "1", true, criterionIndex, 0, [])).ToArray())).ToArray());

    private static string DefaultCheckText(string riskText)
    {
        var text = riskText.Trim().TrimEnd('.');
        return $"Проверить соблюдение требований: {char.ToLowerInvariant(text[0])}{text[1..]}.";
    }

    private static IReadOnlyList<ClassifierApplicabilityRule> DefaultRules(string sectionCode, string criterionCode)
    {
        if (sectionCode == "1") return [new("*", "always", [])];
        return sectionCode switch
        {
            "2" => [new("environmentalAspects|emissionSources|equipment|permits", "contains-any", ["выброс", "атмосфер", "котель", "гпа", "гту", "дэс", "газоочист", "разрешение на выброс"])],
            "3" => [new("environmentalAspects|waterSupply|treatmentFacilities|zones|permits", "contains-any", ["вод", "сброс", "сточ", "очистн", "кос", "лос", "скваж", "водопользован"])],
            "4" => [new("environmentalAspects|wasteStandard|zones|equipment|permits", "contains-any", ["отход", "накоплен", "размещен", "пноолр", "лимит", "гсм"])],
            "5" => [new("environmentalAspects|zones|specialZones", "contains-any", ["зем", "почв", "территор", "рекультивац", "санитар"] )],
            "6" => [new("equipment|waterSupply|permits|zones", "contains-any", ["скваж", "недр", "лицензия на недропользование"])],
            "7" => [new("specialZones|zones|environmentalAspects", "contains-any", ["оопт", "лес", "живот", "биолог", "водоохран", "миграц"])],
            _ => []
        };
    }

    private static Guid StableId(string value)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private static partial IReadOnlyList<ClassifierSeedSection> BuildSections();
}
