using System.Text.RegularExpressions;

internal sealed record FacilityFacts(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields,
    IReadOnlySet<string> ScheduleCriterionCodes)
{
    public IReadOnlyList<string> Values(params string[] names) => names
        .SelectMany(name => Fields.TryGetValue(name, out var values) ? values : [])
        .ToArray();
}

internal static partial class FacilityFactNormalizer
{
    public static FacilityFacts Normalize(FacilityProfileFields profile, string? scheduleCriteria)
    {
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        Add(nameof(profile.Type), profile.Type); Add(nameof(profile.Category), profile.Category);
        Add(nameof(profile.Region), profile.Region); Add(nameof(profile.SpecialZones), profile.SpecialZones);
        Add(nameof(profile.Zones), profile.Zones); Add(nameof(profile.EnvironmentalAspects), profile.EnvironmentalAspects);
        Add(nameof(profile.Equipment), profile.Equipment); Add(nameof(profile.GasTreatment), profile.GasTreatment);
        Add(nameof(profile.TreatmentFacilities), profile.TreatmentFacilities); Add(nameof(profile.WaterSupply), profile.WaterSupply);
        Add(nameof(profile.EmissionSources), profile.EmissionSources); Add(nameof(profile.Permits), profile.Permits);
        Add(nameof(profile.PecProgram), profile.PecProgram); Add(nameof(profile.WasteStandard), profile.WasteStandard);
        Add(nameof(profile.SanitaryZoneProject), profile.SanitaryZoneProject);
        var scheduleCodes = CriterionCodeRegex().Matches(scheduleCriteria ?? string.Empty)
            .Select(match => match.Value.TrimEnd('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(fields, scheduleCodes);

        void Add(string name, string? value) => fields[char.ToLowerInvariant(name[0]) + name[1..]] = Split(value);
    }

    private static IReadOnlyList<string> Split(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value
        .Split(['\r','\n',';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeValue).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string NormalizeValue(string value) => string.Join(' ', value.Trim().ToLowerInvariant()
        .Replace('ё','е').Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"(?<!\d)[1-7]\.[1-9]\d?\.?")]
    private static partial Regex CriterionCodeRegex();
}
