internal sealed class InMemoryAiChecklistRunStore : IAiChecklistRunStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, AiChecklistRunState> runs = [];

    public Task<AiChecklistRunState> CreateAsync(FacilityProfile profile, Guid facilityId, string facilityName, IReadOnlyList<AiChecklistEvidence> evidence, IReadOnlyList<AiChecklistBatchPlan> batches, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AiChecklistRunState(Guid.NewGuid(), profile, facilityId, facilityName, "ready", now, now, evidence,
            batches.Select(item => new AiChecklistBatchState(item.Index, item.Topic, item.EvidenceIds, "pending", 0, null, null, now, [], item.CriterionCodes, item.ApplicabilityReason, item.Query, item.FallbackTitle, item.Section, [])).ToArray(), null);
        lock (gate) runs[run.Id] = run;
        return Task.FromResult(run);
    }

    public Task<AiChecklistRunState?> GetAsync(Guid runId, CancellationToken cancellationToken) { lock (gate) return Task.FromResult(runs.GetValueOrDefault(runId)); }

    public Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run) || run.Status != "ready") return Task.FromResult(false);
            var batch = run.Batches.FirstOrDefault(item => item.Index == batchIndex);
            if (batch is null) return Task.FromResult(false);
            if (batch.Status is "queued" or "running" || batch.Status == "completed" && batch.ItemCount > 0) return Task.FromResult(true);
            Replace(run, batch with { Status = "queued", Error = null, UpdatedAt = DateTimeOffset.UtcNow });
            return Task.FromResult(true);
        }
    }
    public async Task<bool> QueueBatchesAsync(Guid runId, IReadOnlyList<int> batchIndexes, CancellationToken cancellationToken)
    {
        if (batchIndexes.Count == 0) return true;
        foreach (var index in batchIndexes)
            if (!await QueueBatchAsync(runId, index, cancellationToken)) return false;
        return true;
    }

    public Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run) || run.Status is not ("ready" or "stopping")) return Task.FromResult(false);
            var batches = run.Batches.Select(item => item.Status is "pending" or "queued"
                ? item with { Status = "skipped", Error = "Остановлено пользователем.", UpdatedAt = DateTimeOffset.UtcNow }
                : item).ToArray();
            var status = batches.Any(item => item.Status == "running") ? "stopping" : "stopped";
            runs[runId] = run with { Status = status, Batches = batches, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }

    public Task<AiChecklistBatchWork?> ClaimNextBatchAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var run in runs.Values.OrderBy(item => item.CreatedAt))
            {
                var batch = run.Batches.FirstOrDefault(item => item.Status == "queued");
                if (batch is null) continue;
                var claimed = batch with { Status = "running", UpdatedAt = DateTimeOffset.UtcNow };
                Replace(run, claimed);
                return Task.FromResult<AiChecklistBatchWork?>(new(run.Id, run.Facility, claimed, run.Evidence.Where(item => claimed.EvidenceIds.Contains(item.Id)).ToArray()));
            }
            return Task.FromResult<AiChecklistBatchWork?>(null);
        }
    }

    public Task CompleteBatchAsync(Guid runId, int batchIndex, IReadOnlyList<AiGeneratedChecklistItem> items, long durationMs, CancellationToken cancellationToken)
    { lock (gate) { var run = runs[runId]; var batch = run.Batches.Single(item => item.Index == batchIndex); Replace(run, batch with { Status = "completed", Items = items, ItemCount = items.Count, DurationMs = durationMs, Error = null, UpdatedAt = DateTimeOffset.UtcNow }); SettleStop(runId); } return Task.CompletedTask; }
    public Task FailBatchAsync(Guid runId, int batchIndex, string error, long durationMs, CancellationToken cancellationToken)
    { lock (gate) { var run = runs[runId]; var batch = run.Batches.Single(item => item.Index == batchIndex); Replace(run, batch with { Status = "failed", Error = error, DurationMs = durationMs, UpdatedAt = DateTimeOffset.UtcNow }); SettleStop(runId); } return Task.CompletedTask; }
    public Task<bool> BeginFinalizeAsync(Guid runId, CancellationToken cancellationToken)
    { lock (gate) { if (!runs.TryGetValue(runId, out var run) || run.Status is not ("ready" or "stopped" or "finalizing") || run.Batches.Any(item => item.Status is not ("completed" or "failed" or "skipped"))) return Task.FromResult(false); runs[runId] = run with { Status = "finalizing", UpdatedAt = DateTimeOffset.UtcNow }; return Task.FromResult(true); } }
    public Task CompleteFinalizeAsync(Guid runId, Guid checklistId, CancellationToken cancellationToken) { lock (gate) runs[runId] = runs[runId] with { Status = "completed", ChecklistId = checklistId, UpdatedAt = DateTimeOffset.UtcNow }; return Task.CompletedTask; }
    public Task CancelFinalizeAsync(Guid runId, string error, CancellationToken cancellationToken) { lock (gate) runs[runId] = runs[runId] with { Status = "ready", UpdatedAt = DateTimeOffset.UtcNow }; return Task.CompletedTask; }
    public Task ResetInterruptedAsync(CancellationToken cancellationToken) { lock (gate) foreach (var run in runs.Values.ToArray()) { var batches = run.Batches.Select(item => item.Status == "running" ? item with { Status = run.Status == "stopping" ? "skipped" : "queued" } : item).ToArray(); var status = run.Status == "stopping" ? "stopped" : run.Status == "finalizing" ? "ready" : run.Status; runs[run.Id] = run with { Batches = batches, Status = status }; } return Task.CompletedTask; }

    private void Replace(AiChecklistRunState run, AiChecklistBatchState batch)
    {
        var batches = run.Batches.Select(item => item.Index == batch.Index ? batch : item).ToArray();
        runs[run.Id] = run with { Batches = batches, UpdatedAt = DateTimeOffset.UtcNow };
    }
    public Task CompleteCriterionAsync(Guid runId, int batchIndex, IReadOnlyList<AiChecklistEvidence> evidence, IReadOnlyList<AiGeneratedChecklistItem> items, long durationMs, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var run = runs[runId]; var batch = run.Batches.Single(item => item.Index == batchIndex);
            Replace(run, batch with { Status = "completed", ItemCount = items.Count, Items = items.ToArray(), BatchEvidence = evidence.ToArray(), Error = null, DurationMs = durationMs, UpdatedAt = DateTimeOffset.UtcNow });
            SettleStop(runId);
        }
        return Task.CompletedTask;
    }

    private void SettleStop(Guid runId)
    {
        var run = runs[runId];
        if (run.Status == "stopping" && run.Batches.All(item => item.Status != "running" && item.Status != "queued"))
            runs[runId] = run with { Status = "stopped", UpdatedAt = DateTimeOffset.UtcNow };
    }
}
