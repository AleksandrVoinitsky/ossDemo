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
                IsBase(criterion.Code), true, criterionIndex, 0, [])).ToArray())).ToArray());

    private static string DefaultCheckText(string riskText)
    {
        var text = riskText.Trim().TrimEnd('.');
        return $"Проверить соблюдение требований: {char.ToLowerInvariant(text[0])}{text[1..]}.";
    }

    private static IReadOnlyList<ClassifierApplicabilityRule> DefaultRules(string sectionCode, string criterionCode)
    {
        if (IsBase(criterionCode)) return [new("*", "always", [])];
        if (criterionCode == "1.2") return [new("category|permits", "contains-any", ["i", "ii", "кэр", "комплексное экологическое"] )];
        if (criterionCode == "1.4") return [new("environmentalAspects|permits|zones", "contains-any", ["экспертиз", "строитель", "реконструкц"] )];
        if (criterionCode == "1.7") return [new("equipment|permits|environmentalAspects", "contains-any", ["нефт", "гсм", "пларн", "разлив"] )];
        return sectionCode switch
        {
            "2" when criterionCode == "2.4" => [new("gasTreatment|equipment", "contains-any", ["да", "газоочист", "очистк", "гоу"])],
            "2" when criterionCode == "2.5" => [new("sanitaryZoneProject|specialZones|zones", "contains-any", ["да", "сзз", "санитар"] )],
            "2" => [new("environmentalAspects|emissionSources|equipment|permits", "contains-any", ["выброс", "атмосфер", "котель", "гпа", "гту", "дэс", "разрешение на выброс"])],
            "3" when criterionCode is "3.1" or "3.2" => [new("waterSupply|environmentalAspects|permits", "contains-any", ["вод", "сброс", "скваж", "договор водопользования", "разрешение на сброс"] )],
            "3" when criterionCode is "3.3" or "3.4" => [new("environmentalAspects|treatmentFacilities|permits", "contains-any", ["сброс", "сточ", "очистн", "ндс", "разрешение на сброс"] )],
            "3" when criterionCode == "3.6" => [new("waterSupply|equipment|specialZones", "contains-any", ["да", "скваж", "зсо", "санитар"] )],
            "3" when criterionCode is "3.7" or "3.8" => [new("specialZones|zones", "contains-any", ["водоохран", "прибреж"] )],
            "3" when criterionCode == "3.9" => [new("treatmentFacilities|zones", "contains-any", ["да", "очистн", "кос", "лос"] )],
            "3" => [new("environmentalAspects|waterSupply|treatmentFacilities|zones", "contains-any", ["вод", "сброс", "сточ", "очистн", "кос", "лос", "скваж"] )],
            "4" when criterionCode is "4.2" or "4.4" or "4.8" or "4.9" => [new("environmentalAspects|wasteStandard|zones", "contains-any", ["размещен", "полигон", "лимит", "отход"] )],
            "4" when criterionCode == "4.10" => [new("permits|environmentalAspects", "contains-any", ["лиценз", "утилизац", "обезвреж", "размещен"] )],
            "4" when criterionCode == "4.13" => [new("environmentalAspects|zones", "contains-any", ["лом", "металл"] )],
            "4" => [new("environmentalAspects|wasteStandard|zones|equipment", "contains-any", ["отход", "накоплен", "пноолр", "лимит", "гсм"])],
            "5" => [new("environmentalAspects|zones|specialZones", "contains-any", ["зем", "почв", "территор", "рекультивац", "санитар"] )],
            "6" => [new("equipment|waterSupply|permits|zones", "contains-any", ["скваж", "недр", "лицензия на недропользование"])],
            "7" => [new("specialZones|zones|environmentalAspects", "contains-any", ["оопт", "лес", "живот", "биолог", "водоохран", "миграц"])],
            _ => []
        };
    }

    private static bool IsBase(string code) => code is "1.1" or "1.3" or "1.5" or "1.6" or "1.8";

    private static Guid StableId(string value)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private static partial IReadOnlyList<ClassifierSeedSection> BuildSections();
}
