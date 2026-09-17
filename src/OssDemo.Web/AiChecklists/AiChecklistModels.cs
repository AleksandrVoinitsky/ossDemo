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
    IReadOnlyList<AiChecklistVerifiedCitation> Citations)
{
    public IReadOnlyList<string> SourceIds => Citations.Select(item => item.SourceId).ToArray();
}

internal sealed record AiChecklistVerifiedCitation(string SourceId, string Quote);

internal sealed record AiChecklistBatchPlan(
    int Index,
    string Topic,
    IReadOnlyList<string> EvidenceIds,
    int ContextCharacters,
    IReadOnlyList<string>? CriterionCodes = null,
    string? ApplicabilityReason = null,
    string? Query = null,
    string? FallbackTitle = null,
    string? Section = null,
    IReadOnlyList<string>? RequirementIds = null);

internal sealed record AiChecklistBatchState(
    int Index,
    string Topic,
    IReadOnlyList<string> EvidenceIds,
    string Status,
    int ItemCount,
    string? Error,
    long? DurationMs,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AiGeneratedChecklistItem> Items,
    IReadOnlyList<string>? CriterionCodes = null,
    string? ApplicabilityReason = null,
    string? Query = null,
    string? FallbackTitle = null,
    string? Section = null,
    IReadOnlyList<AiChecklistEvidence>? BatchEvidence = null,
    string Stage = "waiting",
    string StageMessage = "Ожидает запуска",
    int FoundSourceCount = 0,
    string DraftOutput = "",
    IReadOnlyList<string>? RequirementIds = null);

internal sealed record AiChecklistDecisionFactSnapshot(string Code, string State, string Details);

internal sealed record AiChecklistClassifierDecisionSnapshot(
    string Code,
    string Outcome,
    string Reason,
    IReadOnlyList<AiChecklistDecisionFactSnapshot> Facts);

internal sealed record AiChecklistCoverageGapSnapshot(string RequirementId, string Reason);

internal sealed record AiChecklistItemTraceSnapshot(
    string Id,
    string Title,
    string Basis,
    IReadOnlyList<string> ClassifierCodes,
    IReadOnlyList<string> RequirementIds,
    string Provenance,
    string Explanation);

internal sealed record AiChecklistRunSnapshot(
    int ProfileSchemaVersion,
    string ProfileVerificationStatus,
    IReadOnlyList<AiChecklistClassifierDecisionSnapshot> ClassifierDecisions,
    IReadOnlyList<string> SelectedRequirementIds,
    IReadOnlyList<string> SelectedItemIds,
    IReadOnlyList<AiChecklistCoverageGapSnapshot> CoverageGaps,
    IReadOnlyDictionary<string, string> CatalogHashes,
    IReadOnlyList<AiChecklistItemTraceSnapshot>? ItemTraces = null,
    Guid? TemplateId = null)
{
    public int IncludedCriterionCount => ClassifierDecisions.Count(item => item.Outcome == "included");
    public int ExcludedCriterionCount => ClassifierDecisions.Count(item => item.Outcome == "excluded");
    public int BlockedCriterionCount => ClassifierDecisions.Count(item => item.Outcome == "blocked_unknown");
    public int SelectedRequirementCount => SelectedRequirementIds.Count;
    public int ExpectedItemCount => SelectedItemIds.Count;
}

internal sealed record AiChecklistRunSnapshotResponse(
    int ProfileSchemaVersion,
    string ProfileVerificationStatus,
    IReadOnlyList<AiChecklistClassifierDecisionSnapshot> ClassifierDecisions,
    IReadOnlyList<string> SelectedRequirementIds,
    IReadOnlyList<string> SelectedItemIds,
    IReadOnlyList<AiChecklistCoverageGapSnapshot> CoverageGaps,
    IReadOnlyDictionary<string, string> CatalogHashes,
    Guid? TemplateId)
{
    public int IncludedCriterionCount => ClassifierDecisions.Count(item => item.Outcome == "included");
    public int ExcludedCriterionCount => ClassifierDecisions.Count(item => item.Outcome == "excluded");
    public int BlockedCriterionCount => ClassifierDecisions.Count(item => item.Outcome == "blocked_unknown");
    public int SelectedRequirementCount => SelectedRequirementIds.Count;
    public int ExpectedItemCount => SelectedItemIds.Count;

    public static AiChecklistRunSnapshotResponse From(AiChecklistRunSnapshot snapshot) => new(
        snapshot.ProfileSchemaVersion, snapshot.ProfileVerificationStatus, snapshot.ClassifierDecisions,
        snapshot.SelectedRequirementIds, snapshot.SelectedItemIds, snapshot.CoverageGaps, snapshot.CatalogHashes, snapshot.TemplateId);
}

internal sealed record AiChecklistRunState(
    Guid Id,
    FacilityProfile Facility,
    Guid FacilityId,
    string FacilityName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AiChecklistEvidence> Evidence,
    IReadOnlyList<AiChecklistBatchState> Batches,
    Guid? ChecklistId,
    AiChecklistRunSnapshot? Snapshot = null,
    Guid? DraftId = null);

internal sealed record AiChecklistRunResponse(
    Guid Id,
    FacilityProfile Facility,
    Guid FacilityId,
    string FacilityName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AiChecklistBatchState> Batches,
    Guid? ChecklistId,
    AiChecklistRunSnapshotResponse? Snapshot,
    Guid? DraftId)
{
    public static AiChecklistRunResponse From(AiChecklistRunState run) => new(
        run.Id, run.Facility, run.FacilityId, run.FacilityName, run.Status, run.CreatedAt, run.UpdatedAt,
        run.Batches, run.ChecklistId,
        run.Snapshot is null ? null : AiChecklistRunSnapshotResponse.From(run.Snapshot), run.DraftId);
}

internal sealed record AiChecklistTracePage(
    IReadOnlyList<AiChecklistItemTraceSnapshot> Items,
    int Total,
    int Offset,
    int Limit);

internal sealed record AiChecklistRequirementSnapshot(
    string Id,
    IReadOnlyList<string> ClassifierCodes,
    IReadOnlyList<string> Levels,
    string Basis,
    string Requirement);

internal sealed record AiChecklistRequirementPage(
    IReadOnlyList<AiChecklistRequirementSnapshot> Items,
    int Total,
    int Offset,
    int Limit);

internal sealed record AiChecklistBatchWork(
    Guid RunId,
    FacilityProfile Facility,
    AiChecklistBatchState Batch,
    IReadOnlyList<AiChecklistEvidence> Evidence);

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

internal sealed record AiGeneratedDraftItem(
    string Section,
    string Title,
    string Basis,
    string Note,
    IReadOnlyList<string>? CriterionCodes = null,
    string? SourceLabel = null);

internal sealed record CreateAiChecklistDraftRequest(
    Guid FacilityId,
    string FacilityName,
    string Name,
    IReadOnlyList<AiGeneratedDraftItem> Items,
    Guid? RunId = null);

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
