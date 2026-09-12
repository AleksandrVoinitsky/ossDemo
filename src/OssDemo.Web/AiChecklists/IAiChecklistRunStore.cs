internal interface IAiChecklistRunStore
{
    Task<AiChecklistRunState> CreateAsync(FacilityProfile profile, Guid facilityId, string facilityName, IReadOnlyList<AiChecklistEvidence> evidence, IReadOnlyList<AiChecklistBatchPlan> batches, CancellationToken cancellationToken);
    Task<AiChecklistRunState?> GetAsync(Guid runId, CancellationToken cancellationToken);
    Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken);
    Task<bool> QueueBatchesAsync(Guid runId, IReadOnlyList<int> batchIndexes, CancellationToken cancellationToken);
    Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken);
    Task<AiChecklistBatchWork?> ClaimNextBatchAsync(CancellationToken cancellationToken);
    Task UpdateProgressAsync(Guid runId, int batchIndex, string stage, string message, int foundSourceCount, string draftOutput, CancellationToken cancellationToken);
    Task CompleteBatchAsync(Guid runId, int batchIndex, IReadOnlyList<AiGeneratedChecklistItem> items, long durationMs, CancellationToken cancellationToken);
    Task CompleteCriterionAsync(Guid runId, int batchIndex, IReadOnlyList<AiChecklistEvidence> evidence, IReadOnlyList<AiGeneratedChecklistItem> items, long durationMs, CancellationToken cancellationToken);
    Task FailBatchAsync(Guid runId, int batchIndex, string error, long durationMs, CancellationToken cancellationToken);
    Task<bool> BeginFinalizeAsync(Guid runId, CancellationToken cancellationToken);
    Task CompleteFinalizeAsync(Guid runId, Guid checklistId, CancellationToken cancellationToken);
    Task CancelFinalizeAsync(Guid runId, string error, CancellationToken cancellationToken);
    Task ResetInterruptedAsync(CancellationToken cancellationToken);
}
