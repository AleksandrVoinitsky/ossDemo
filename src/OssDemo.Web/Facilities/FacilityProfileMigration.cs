public static class FacilityProfileMigration
{
    public static readonly FacilityLegacySource EmptyLegacySource = new("", "", "", "", "", "", "", "", "", "", "", "", "");

    public static FacilityProfileV2 FromLegacy(string slug, FacilityProfileFields legacy)
    {
        var result = FacilityProfileV2.CreateEmpty(slug, legacy.ShortName);
        result.LegacySource = new(
            legacy.Type, legacy.SpecialZones, legacy.Zones, legacy.EnvironmentalAspects,
            legacy.Equipment, legacy.GasTreatment, legacy.TreatmentFacilities, legacy.WaterSupply,
            legacy.EmissionSources, legacy.Permits, legacy.PecProgram, legacy.WasteStandard,
            legacy.SanitaryZoneProject);

        MarkPresent(result, "air.emissions", [legacy.EnvironmentalAspects, legacy.EmissionSources], ["выброс", "атмосфер"]);
        MarkPresent(result, "air.gasTreatment", [legacy.GasTreatment], ["газоочист", "гоу"]);
        MarkPresent(result, "water.intake", [legacy.WaterSupply, legacy.Equipment], ["водоснабж", "водозабор", "скваж"]);
        MarkPresent(result, "water.discharge", [legacy.EnvironmentalAspects, legacy.Permits], ["сброс", "сточн"]);
        MarkPresent(result, "water.treatment", [legacy.TreatmentFacilities, legacy.Zones], ["очистн", "кос", "лос"]);
        MarkPresent(result, "waste.generation", [legacy.EnvironmentalAspects, legacy.WasteStandard], ["отход"]);
        MarkPresent(result, "waste.disposalSite", [legacy.Zones, legacy.Permits], ["полигон", "размещени"]);
        MarkPresent(result, "land.disturbance", [legacy.EnvironmentalAspects, legacy.Zones], ["земель", "почв", "рекультивац"]);
        MarkPresent(result, "subsoil.wells", [legacy.Equipment, legacy.WaterSupply, legacy.Permits], ["скваж", "недропольз"]);
        MarkPresent(result, "nature.forest", [legacy.SpecialZones, legacy.Zones], ["лес"]);
        MarkPresent(result, "nature.oopt", [legacy.SpecialZones], ["оопт", "особо охраняем"]);
        MarkPresent(result, "zone.waterProtection", [legacy.SpecialZones, legacy.Zones], ["водоохран", "прибреж"]);
        if (!string.IsNullOrWhiteSpace(legacy.Type)) result.ObjectTypeCodes.Add(legacy.Type.Trim());
        return result;
    }

    private static void MarkPresent(FacilityProfileV2 target, string code, IReadOnlyList<string> values, IReadOnlyList<string> markers)
    {
        var matched = values.FirstOrDefault(value => markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(matched)) target.Features[code] = new(FacilityFactState.Present, matched.Trim());
    }
}
