using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal sealed class PostgresAiChecklistRunStore(IConfiguration configuration) : IAiChecklistRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AiChecklistRunState> CreateAsync(FacilityProfile profile, Guid facilityId, string facilityName, IReadOnlyList<AiChecklistEvidence> evidence, IReadOnlyList<AiChecklistBatchPlan> batches, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var runId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("INSERT INTO app_ai_checklist_runs(id,facility_id,facility_name,facility_profile) VALUES(@id,@facilityId,@facilityName,CAST(@profile AS jsonb))", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", runId); insert.Parameters.AddWithValue("facilityId", facilityId); insert.Parameters.AddWithValue("facilityName", facilityName); insert.Parameters.AddWithValue("profile", JsonSerializer.Serialize(profile, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            await using var insert = new NpgsqlCommand("INSERT INTO app_ai_checklist_evidence(run_id,evidence_id,query_label,document_title,source_label,content,score,position) VALUES(@runId,@id,@query,@document,@source,@content,@score,@position)", connection, transaction);
            insert.Parameters.AddWithValue("runId", runId); insert.Parameters.AddWithValue("id", item.Id); insert.Parameters.AddWithValue("query", item.QueryLabel); insert.Parameters.AddWithValue("document", item.DocumentTitle); insert.Parameters.AddWithValue("source", item.SourceLabel); insert.Parameters.AddWithValue("content", item.Text); insert.Parameters.AddWithValue("score", item.Score); insert.Parameters.AddWithValue("position", index);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var batch in batches)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO app_ai_checklist_batches(run_id,batch_index,topic,evidence_ids,criterion_codes,applicability_reason,search_query,fallback_title,classifier_section) VALUES(@runId,@index,@topic,@ids,@codes,@reason,@query,@fallback,@section)", connection, transaction);
            insert.Parameters.AddWithValue("runId", runId); insert.Parameters.AddWithValue("index", batch.Index); insert.Parameters.AddWithValue("topic", batch.Topic); insert.Parameters.AddWithValue("ids", batch.EvidenceIds.ToArray());
            insert.Parameters.AddWithValue("codes", batch.CriterionCodes?.ToArray() ?? []); insert.Parameters.AddWithValue("reason", (object?)batch.ApplicabilityReason ?? DBNull.Value);
            insert.Parameters.AddWithValue("query", (object?)batch.Query ?? DBNull.Value); insert.Parameters.AddWithValue("fallback", (object?)batch.FallbackTitle ?? DBNull.Value); insert.Parameters.AddWithValue("section", (object?)batch.Section ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(runId, cancellationToken))!;
    }

    public async Task<AiChecklistRunState?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT facility_id,facility_name,facility_profile::text,status,created_at,updated_at,checklist_id FROM app_ai_checklist_runs WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", runId);
        FacilityProfile profile; Guid facilityId; string facilityName; string status; DateTimeOffset createdAt; DateTimeOffset updatedAt; Guid? checklistId;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            facilityId = reader.GetGuid(0); facilityName = reader.GetString(1); profile = JsonSerializer.Deserialize<FacilityProfile>(reader.GetString(2), JsonOptions)!; status = reader.GetString(3); createdAt = reader.GetFieldValue<DateTimeOffset>(4); updatedAt = reader.GetFieldValue<DateTimeOffset>(5); checklistId = reader.IsDBNull(6) ? null : reader.GetGuid(6);
        }
        var evidence = new List<AiChecklistEvidence>();
        await using (var evidenceCommand = new NpgsqlCommand("SELECT evidence_id,query_label,document_title,source_label,content,score FROM app_ai_checklist_evidence WHERE run_id=@id ORDER BY position", connection))
        {
            evidenceCommand.Parameters.AddWithValue("id", runId); await using var reader = await evidenceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) evidence.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDouble(5)));
        }
        var batches = new List<AiChecklistBatchState>();
        await using (var batchCommand = new NpgsqlCommand("SELECT batch_index,topic,evidence_ids,status,items::text,error,duration_ms,updated_at,criterion_codes,applicability_reason,search_query,fallback_title,classifier_section,evidence::text FROM app_ai_checklist_batches WHERE run_id=@id ORDER BY batch_index", connection))
        {
            batchCommand.Parameters.AddWithValue("id", runId); await using var reader = await batchCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var items = JsonSerializer.Deserialize<AiGeneratedChecklistItem[]>(reader.GetString(4), JsonOptions) ?? [];
                var batchEvidence = JsonSerializer.Deserialize<AiChecklistEvidence[]>(reader.GetString(13), JsonOptions) ?? [];
                batches.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetFieldValue<string[]>(2),reader.GetString(3),items.Length,reader.IsDBNull(5)?null:reader.GetString(5),reader.IsDBNull(6)?null:reader.GetInt64(6),reader.GetFieldValue<DateTimeOffset>(7),items,
                    reader.GetFieldValue<string[]>(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetString(12),batchEvidence));
            }
        }
        return new(runId,profile,facilityId,facilityName,status,createdAt,updatedAt,evidence,batches,checklistId);
    }

    public async Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken)
        => await QueueBatchesAsync(runId, [batchIndex], cancellationToken);

    public async Task<bool> QueueBatchesAsync(Guid runId, IReadOnlyList<int> batchIndexes, CancellationToken cancellationToken)
    {
        var indexes = batchIndexes.Distinct().ToArray();
        if (indexes.Length == 0) return true;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockRun = new NpgsqlCommand("SELECT 1 FROM app_ai_checklist_runs WHERE id=@runId AND status='ready' FOR UPDATE", connection, transaction))
        {
            lockRun.Parameters.AddWithValue("runId", runId);
            if (await lockRun.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await using var command = new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status=CASE WHEN status IN ('pending','failed') OR (status='completed' AND jsonb_array_length(items)=0) THEN 'queued' ELSE status END,error=NULL,updated_at=now() WHERE run_id=@runId AND batch_index=ANY(@indexes)", connection, transaction);
        command.Parameters.AddWithValue("runId",runId);
        command.Parameters.AddWithValue("indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer, indexes);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != indexes.Length)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockRun = new NpgsqlCommand("SELECT status FROM app_ai_checklist_runs WHERE id=@runId AND status IN ('ready','stopping') FOR UPDATE", connection, transaction))
        {
            lockRun.Parameters.AddWithValue("runId", runId);
            if (await lockRun.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await using (var skip = new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status='skipped',error='Остановлено пользователем.',updated_at=now() WHERE run_id=@runId AND status IN ('pending','queued')", connection, transaction))
        {
            skip.Parameters.AddWithValue("runId", runId);
            await skip.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand("UPDATE app_ai_checklist_runs SET status=CASE WHEN EXISTS(SELECT 1 FROM app_ai_checklist_batches WHERE run_id=@runId AND status='running') THEN 'stopping' ELSE 'stopped' END,updated_at=now() WHERE id=@runId", connection, transaction))
        {
            update.Parameters.AddWithValue("runId", runId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AiChecklistBatchWork?> ClaimNextBatchAsync(CancellationToken cancellationToken)
    {
        Guid? runId = null;
        var batchIndex = 0;
        await using (var connection = await OpenAsync(cancellationToken))
        await using (var claim = new NpgsqlCommand("WITH next_batch AS (SELECT batch.run_id,batch.batch_index FROM app_ai_checklist_batches batch JOIN app_ai_checklist_runs run ON run.id=batch.run_id WHERE batch.status='queued' AND run.status='ready' ORDER BY batch.created_at,batch.batch_index FOR UPDATE OF batch SKIP LOCKED LIMIT 1) UPDATE app_ai_checklist_batches batch SET status='running',updated_at=now() FROM next_batch WHERE batch.run_id=next_batch.run_id AND batch.batch_index=next_batch.batch_index RETURNING batch.run_id,batch.batch_index", connection))
        await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                runId = reader.GetGuid(0);
                batchIndex = reader.GetInt32(1);
            }
        }
        if (runId is null) return null;
        var claimedRunId = runId.Value;
        var run=await GetAsync(claimedRunId,cancellationToken);var batch=run!.Batches.Single(item=>item.Index==batchIndex);var workEvidence=batch.BatchEvidence is { Count: > 0 }?batch.BatchEvidence:run.Evidence.Where(item=>batch.EvidenceIds.Contains(item.Id)).ToArray();return new(claimedRunId,run.Facility,batch,workEvidence);
    }

    public Task CompleteBatchAsync(Guid runId,int batchIndex,IReadOnlyList<AiGeneratedChecklistItem> items,long durationMs,CancellationToken ct)=>UpdateBatchAsync(runId,batchIndex,"completed",JsonSerializer.Serialize(items,JsonOptions),null,durationMs,ct);
    public async Task CompleteCriterionAsync(Guid runId,int batchIndex,IReadOnlyList<AiChecklistEvidence> evidence,IReadOnlyList<AiGeneratedChecklistItem> items,long durationMs,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status='completed',items=CAST(@items AS jsonb),evidence=CAST(@evidence AS jsonb),error=NULL,duration_ms=@duration,updated_at=now() WHERE run_id=@runId AND batch_index=@index; UPDATE app_ai_checklist_runs SET status='stopped',updated_at=now() WHERE id=@runId AND status='stopping' AND NOT EXISTS(SELECT 1 FROM app_ai_checklist_batches WHERE run_id=@runId AND status IN ('running','queued'))",connection);command.Parameters.AddWithValue("items",JsonSerializer.Serialize(items,JsonOptions));command.Parameters.AddWithValue("evidence",JsonSerializer.Serialize(evidence,JsonOptions));command.Parameters.AddWithValue("duration",durationMs);command.Parameters.AddWithValue("runId",runId);command.Parameters.AddWithValue("index",batchIndex);await command.ExecuteNonQueryAsync(ct);}
    public Task FailBatchAsync(Guid runId,int batchIndex,string error,long durationMs,CancellationToken ct)=>UpdateBatchAsync(runId,batchIndex,"failed","[]",error,durationMs,ct);
    private async Task UpdateBatchAsync(Guid runId,int index,string status,string items,string? error,long durationMs,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status=@status,items=CAST(@items AS jsonb),error=@error,duration_ms=@duration,updated_at=now() WHERE run_id=@runId AND batch_index=@index; UPDATE app_ai_checklist_runs SET status='stopped',updated_at=now() WHERE id=@runId AND status='stopping' AND NOT EXISTS(SELECT 1 FROM app_ai_checklist_batches WHERE run_id=@runId AND status IN ('running','queued'))",connection);command.Parameters.AddWithValue("status",status);command.Parameters.AddWithValue("items",items);command.Parameters.AddWithValue("error",(object?)error??DBNull.Value);command.Parameters.AddWithValue("duration",durationMs);command.Parameters.AddWithValue("runId",runId);command.Parameters.AddWithValue("index",index);await command.ExecuteNonQueryAsync(ct);}
    public async Task<bool> BeginFinalizeAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? status;
        await using (var lockRun = new NpgsqlCommand("SELECT status FROM app_ai_checklist_runs WHERE id=@id AND status IN ('ready','stopped','finalizing') FOR UPDATE", connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", runId);
            status = await lockRun.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (status is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using (var incomplete = new NpgsqlCommand("SELECT 1 FROM app_ai_checklist_batches WHERE run_id=@id AND status NOT IN ('completed','failed','skipped') LIMIT 1", connection, transaction))
        {
            incomplete.Parameters.AddWithValue("id", runId);
            if (await incomplete.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await using (var update = new NpgsqlCommand("UPDATE app_ai_checklist_runs SET status='finalizing',updated_at=now() WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("id", runId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    public async Task CompleteFinalizeAsync(Guid runId,Guid checklistId,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_runs SET status='completed',checklist_id=@checklistId,updated_at=now() WHERE id=@id",connection);command.Parameters.AddWithValue("id",runId);command.Parameters.AddWithValue("checklistId",checklistId);await command.ExecuteNonQueryAsync(ct);}
    public async Task CancelFinalizeAsync(Guid runId,string error,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_runs SET status='ready',updated_at=now() WHERE id=@id AND status='finalizing'",connection);command.Parameters.AddWithValue("id",runId);await command.ExecuteNonQueryAsync(ct);}
    public async Task ResetInterruptedAsync(CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_batches batch SET status=CASE WHEN run.status='stopping' THEN 'skipped' ELSE 'queued' END,error=CASE WHEN run.status='stopping' THEN 'Остановлено пользователем.' ELSE error END,updated_at=now() FROM app_ai_checklist_runs run WHERE batch.run_id=run.id AND batch.status='running'; UPDATE app_ai_checklist_runs SET status='stopped',updated_at=now() WHERE status='stopping'; UPDATE app_ai_checklist_runs SET status='ready',updated_at=now() WHERE status='finalizing' AND checklist_id IS NULL",connection);await command.ExecuteNonQueryAsync(ct);}
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct){var connection=new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")??throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));await connection.OpenAsync(ct);return connection;}
}
