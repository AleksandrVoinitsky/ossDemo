internal sealed record AiChecklistSearchQuery(string Key, string Label, string Query);

internal sealed record AiChecklistEvidence(
    string Id,
    string QueryLabel,
    string DocumentTitle,
    string SourceLabel,
    string Text,
    double Score);

internal sealed record AiGeneratedChecklistItem(
    string Section,
    string Title,
    string Reason,
    double Confidence,
    IReadOnlyList<string> SourceIds);

internal sealed record AiChecklistSynthesis(
    string Name,
    IReadOnlyList<AiGeneratedChecklistItem> Items);

internal sealed record AiChecklistAnalysis(
    FacilityProfile Facility,
    IReadOnlyList<AiChecklistSearchQuery> Queries);

internal sealed record AiChecklistSearchPreview(
    FacilityProfile Facility,
    IReadOnlyList<AiChecklistSearchQuery> Queries,
    IReadOnlyList<AiChecklistEvidence> Evidence);

internal sealed record AiGeneratedDraftItem(string Section, string Title, string Basis, string Note);

internal sealed record CreateAiChecklistDraftRequest(
    Guid FacilityId,
    string FacilityName,
    string Name,
    IReadOnlyList<AiGeneratedDraftItem> Items);

internal sealed record AiChecklistSynthesisProfile(
    string ShortName,
    string FullName,
    string Type,
    string Category,
    string Region,
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
    string SanitaryZoneProject)
{
    public static AiChecklistSynthesisProfile From(FacilityProfileFields profile) => new(
        profile.ShortName, profile.FullName, profile.Type, profile.Category, profile.Region,
        profile.SpecialZones, profile.Zones, profile.EnvironmentalAspects, profile.Equipment,
        profile.GasTreatment, profile.TreatmentFacilities, profile.WaterSupply, profile.EmissionSources,
        profile.Permits, profile.PecProgram, profile.WasteStandard, profile.SanitaryZoneProject);
}

internal sealed class AiChecklistGenerationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
