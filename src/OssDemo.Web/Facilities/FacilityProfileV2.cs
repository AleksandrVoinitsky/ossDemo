using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<FacilityFactState>))]
public enum FacilityFactState
{
    Unknown,
    Present,
    Absent
}

public sealed record FacilityFeatureFact(FacilityFactState State, string Details = "");

public sealed record FacilityDocumentFact(
    string TypeCode,
    FacilityFactState State,
    string Number = "",
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    string Details = "");

public sealed record FacilityLegacySource(
    string Type,
    string SpecialZones,
    string Zones,
    string EnvironmentalAspects,
    string Equipment,
    string GasTreatment,
    string TreatmentFacilities,
    string WaterSupply,
    string EmissionSources,
    string Permits,
    string PecProgram,
    string WasteStandard,
    string SanitaryZoneProject);

public sealed class FacilityProfileV2
{
    public const int CurrentSchemaVersion = 2;

    public static readonly IReadOnlyList<string> RequiredFeatureCodes =
    [
        "air.emissions",
        "air.gasTreatment",
        "water.intake",
        "water.discharge",
        "water.treatment",
        "waste.generation",
        "waste.disposalSite",
        "land.disturbance",
        "subsoil.wells",
        "nature.forest",
        "nature.oopt",
        "zone.waterProtection"
    ];

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Slug { get; init; } = "";
    public string ShortName { get; init; } = "";
    public string VerificationStatus { get; set; } = "needs_review";
    public DateTimeOffset? VerifiedAt { get; set; }
    public string VerifiedBy { get; set; } = "";
    public List<string> ObjectTypeCodes { get; init; } = [];
    public Dictionary<string, FacilityFeatureFact> Features { get; init; } = CreateUnknownFeatures();
    public List<FacilityDocumentFact> Documents { get; init; } = [];
    public FacilityLegacySource LegacySource { get; set; } = FacilityProfileMigration.EmptyLegacySource;

    public static FacilityProfileV2 CreateEmpty(string slug, string shortName) => new()
    {
        Slug = slug,
        ShortName = shortName
    };

    private static Dictionary<string, FacilityFeatureFact> CreateUnknownFeatures() =>
        RequiredFeatureCodes.ToDictionary(code => code, _ => new FacilityFeatureFact(FacilityFactState.Unknown), StringComparer.OrdinalIgnoreCase);
}
