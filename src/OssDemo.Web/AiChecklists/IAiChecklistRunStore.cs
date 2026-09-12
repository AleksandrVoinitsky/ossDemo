internal interface IAiChecklistRunStore
{
    Task<AiChecklistRunState> CreateAsync(FacilityProfile profile, Guid facilityId, string facilityName, IReadOnlyList<AiChecklistEvidence> evidence, IReadOnlyList<AiChecklistBatchPlan> batches, CancellationToken cancellationToken);
    Task<AiChecklistRunState?> GetAsync(Guid runId, CancellationToken cancellationToken);
    Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken);
    Task<AiChecklistBatchWork?> ClaimNextBatchAsync(CancellationToken cancellationToken);
    Task CompleteBatchAsync(Guid runId, int batchIndex, IReadOnlyList<AiGeneratedChecklistItem> items, long durationMs, CancellationToken cancellationToken);
    Task FailBatchAsync(Guid runId, int batchIndex, string error, long durationMs, CancellationToken cancellationToken);
    Task<bool> BeginFinalizeAsync(Guid runId, CancellationToken cancellationToken);
    Task CompleteFinalizeAsync(Guid runId, Guid checklistId, CancellationToken cancellationToken);
    Task CancelFinalizeAsync(Guid runId, string error, CancellationToken cancellationToken);
    Task ResetInterruptedAsync(CancellationToken cancellationToken);
}
