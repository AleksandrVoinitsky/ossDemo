internal static class AiChecklistQueryPlanner
{
    private const int MaxQueries = 5;
    private const int MaxQueryLength = 500;

    public static IReadOnlyList<AiChecklistSearchQuery> Build(FacilityProfile facility)
    {
        var profile = facility.Profile;
        var identity = Join(profile.Type, profile.Category, profile.Region);
        var candidates = new[]
        {
            Create("base", "Общие требования и документы", "обязательные экологические требования разрешения ПЭК нормативы проверка объекта", identity, profile.Permits, profile.PecProgram),
            Create("aspects", "Экологические аспекты", "требования и пункты проверки", profile.EnvironmentalAspects, identity),
            Create("emissions", "Атмосферный воздух", "выбросы источники выбросов требования контроль", profile.EmissionSources, profile.Equipment, identity),
            Create("waste", "Отходы и территория", "обращение с отходами места накопления санитарно защитные и специальные зоны", profile.WasteStandard, profile.SpecialZones, profile.Zones, identity),
            Create("water", "Вода и очистные сооружения", "водоснабжение водоотведение очистные сооружения газоочистка требования", profile.WaterSupply, profile.TreatmentFacilities, profile.GasTreatment, identity)
        };

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Query))
            .DistinctBy(item => Normalize(item.Query), StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToArray();
    }

    public static AiChecklistSearchQuery Build(ApplicableClassifierCriterion match, FacilityFacts facts)
    {
        var criterion = match.Criterion;
        var ruleFields = criterion.ApplicabilityRules.SelectMany(rule => rule.Field.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var context = ruleFields.Length == 0 || ruleFields.Contains("*")
            ? facts.Values("type", "category", "environmentalAspects", "permits")
            : facts.Values(ruleFields);
        var query = Compact(Join(
            $"критерий {criterion.Code}", criterion.RiskText, criterion.SearchTerms,
            string.Join(' ', criterion.SourceHints), match.MatchedValue, string.Join(' ', context)));
        return new(criterion.Code, $"{criterion.Code} · {match.Section.Title}", query);
    }

    private static AiChecklistSearchQuery Create(string key, string label, params string?[] parts) =>
        new(key, label, Compact(Join(parts)));

    private static string Join(params string?[] parts) => string.Join(" ", parts
        .SelectMany(Split)
        .Where(value => !string.Equals(value, "Не указано", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string value) => string.Join(' ', value.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Compact(string value)
    {
        var normalized = Normalize(value);
        return normalized.Length <= MaxQueryLength ? normalized : normalized[..MaxQueryLength];
    }
}
