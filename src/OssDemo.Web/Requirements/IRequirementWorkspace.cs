internal interface IRequirementWorkspace
{
    Task<ResolvedCriterionRequirements?> ResolveCriterionAsync(Guid criterionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResolvedRequirement>> SearchAsync(string? query, int limit, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ResolvedRequirement>> SaveRevisionAsync(
        string requirementId,
        RequirementRevisionWrite request,
        string actor,
        CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ResolvedCriterionRequirements>> SetLinkAsync(
        Guid criterionId,
        string requirementId,
        string? action,
        string actor,
        CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ResolvedCriterionRequirements>> ClearLinkAsync(
        Guid criterionId,
        string requirementId,
        string actor,
        CancellationToken cancellationToken);
}
