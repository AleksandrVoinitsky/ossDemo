internal static class AiChecklistFallbackBuilder
{
    public static IReadOnlyList<AiGeneratedDraftItem> Build(FacilityProfile facility)
    {
        var profile = facility.Profile;
        var description = string.Join(' ', new[]
        {
            profile.Type, profile.Category, profile.SpecialZones, profile.Zones, profile.EnvironmentalAspects,
            profile.Equipment, profile.GasTreatment, profile.TreatmentFacilities, profile.WaterSupply,
            profile.EmissionSources, profile.Permits, profile.PecProgram, profile.WasteStandard,
            profile.SanitaryZoneProject
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var sections = ChecklistSeedData.Templates[0].Sections;
        var selected = new List<ChecklistSeedSection>();
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Add("Общие вопросы", 6);
        if (Matches(description, "зем", "почв", "территор", "рекультивац")) Add("Земель", 4);
        if (Matches(description, "вод", "сточ", "очистн", "канализац", "скваж")) Add("водных", 5);
        if (Matches(description, "отход", "накоплен", "гсм", "склад")) Add("отход", 5);
        if (Matches(description, "выброс", "атмосфер", "котель", "гту", "гпа", "факел", "дизел", "газоочист")) Add("атмосфер", 5);
        if (Matches(description, "скваж", "недр", "недропольз")) Add("недр", 3);
        if (Matches(description, "охранн", "санитар", "лес", "живот")) Add("животного", 2);

        return selected
            .SelectMany(section => section.Items.Take(LimitFor(section.Title)).Select(item => new AiGeneratedDraftItem(
                section.Title,
                item.Title,
                item.Basis,
                $"Базовый пункт подобран по составу объекта «{facility.Profile.ShortName}» из примеров чек-листов проекта; проверьте применимость.")))
            .DistinctBy(item => item.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        void Add(string titlePart, int limit)
        {
            var section = sections.FirstOrDefault(item => item.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase));
            if (section is not null && selected.All(item => item.Title != section.Title))
            {
                limits[section.Title] = limit;
                selected.Add(section);
            }
        }

        int LimitFor(string title) => limits.TryGetValue(title, out var value) ? value : 3;
    }

    private static bool Matches(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
