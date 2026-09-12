internal static class AiChecklistQueryPlanner
{
    private const int MaxQueries = 8;

    public static IReadOnlyList<AiChecklistSearchQuery> Build(FacilityProfile facility)
    {
        var profile = facility.Profile;
        var identity = Join(profile.Type, profile.Category, profile.Region);
        var candidates = new[]
        {
            Create("base", "Общие требования", "обязательные экологические требования проверка объекта", identity),
            Create("aspects", "Экологические аспекты", "требования и пункты проверки", profile.EnvironmentalAspects, identity),
            Create("emissions", "Атмосферный воздух", "выбросы источники выбросов требования контроль", profile.EmissionSources, profile.Equipment, identity),
            Create("waste", "Отходы", "обращение с отходами места накопления учет требования", profile.EnvironmentalAspects, identity),
            Create("water", "Водопользование", "водоснабжение водоотведение очистные сооружения требования", profile.WaterSupply, profile.TreatmentFacilities, identity),
            Create("permits", "Разрешительная документация", "разрешения нормативы отчетность сроки действия проверка", profile.Permits, profile.PecProgram, profile.WasteStandard),
            Create("zones", "Территория и зоны", "санитарно защитная зона специальные зоны требования", profile.SpecialZones, profile.Zones, profile.SanitaryZoneProject),
            Create("equipment", "Оборудование", "экологические требования эксплуатация оборудования производственный контроль", profile.Equipment, profile.GasTreatment, identity)
        };

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Query))
            .DistinctBy(item => Normalize(item.Query), StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToArray();
    }

    private static AiChecklistSearchQuery Create(string key, string label, params string?[] parts) =>
        new(key, label, Join(parts));

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
}
