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
            await using var insert = new NpgsqlCommand("INSERT INTO app_ai_checklist_batches(run_id,batch_index,topic,evidence_ids) VALUES(@runId,@index,@topic,@ids)", connection, transaction);
            insert.Parameters.AddWithValue("runId", runId); insert.Parameters.AddWithValue("index", batch.Index); insert.Parameters.AddWithValue("topic", batch.Topic); insert.Parameters.AddWithValue("ids", batch.EvidenceIds.ToArray());
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
        await using (var batchCommand = new NpgsqlCommand("SELECT batch_index,topic,evidence_ids,status,items::text,error,duration_ms,updated_at FROM app_ai_checklist_batches WHERE run_id=@id ORDER BY batch_index", connection))
        {
            batchCommand.Parameters.AddWithValue("id", runId); await using var reader = await batchCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var items = JsonSerializer.Deserialize<AiGeneratedChecklistItem[]>(reader.GetString(4), JsonOptions) ?? [];
                batches.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetFieldValue<string[]>(2),reader.GetString(3),items.Length,reader.IsDBNull(5)?null:reader.GetString(5),reader.IsDBNull(6)?null:reader.GetInt64(6),reader.GetFieldValue<DateTimeOffset>(7),items));
            }
        }
        return new(runId,profile,facilityId,facilityName,status,createdAt,updatedAt,evidence,batches,checklistId);
    }

    public async Task<bool> QueueBatchAsync(Guid runId, int batchIndex, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status=CASE WHEN status IN ('pending','failed') THEN 'queued' ELSE status END,error=NULL,updated_at=now() WHERE run_id=@runId AND batch_index=@index RETURNING 1", connection);
        command.Parameters.AddWithValue("runId",runId); command.Parameters.AddWithValue("index",batchIndex);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<AiChecklistBatchWork?> ClaimNextBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid runId; int batchIndex;
        await using (var claim = new NpgsqlCommand("SELECT run_id,batch_index FROM app_ai_checklist_batches WHERE status='queued' ORDER BY created_at,batch_index FOR UPDATE SKIP LOCKED LIMIT 1", connection, transaction))
        { await using var reader=await claim.ExecuteReaderAsync(cancellationToken); if(!await reader.ReadAsync(cancellationToken)){await transaction.RollbackAsync(cancellationToken);return null;} runId=reader.GetGuid(0);batchIndex=reader.GetInt32(1); }
        await using (var update=new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status='running',updated_at=now() WHERE run_id=@runId AND batch_index=@index",connection,transaction)){update.Parameters.AddWithValue("runId",runId);update.Parameters.AddWithValue("index",batchIndex);await update.ExecuteNonQueryAsync(cancellationToken);} await transaction.CommitAsync(cancellationToken);
        var run=await GetAsync(runId,cancellationToken);var batch=run!.Batches.Single(item=>item.Index==batchIndex);return new(runId,run.Facility,batch,run.Evidence.Where(item=>batch.EvidenceIds.Contains(item.Id)).ToArray());
    }

    public Task CompleteBatchAsync(Guid runId,int batchIndex,IReadOnlyList<AiGeneratedChecklistItem> items,long durationMs,CancellationToken ct)=>UpdateBatchAsync(runId,batchIndex,"completed",JsonSerializer.Serialize(items,JsonOptions),null,durationMs,ct);
    public Task FailBatchAsync(Guid runId,int batchIndex,string error,long durationMs,CancellationToken ct)=>UpdateBatchAsync(runId,batchIndex,"failed","[]",error,durationMs,ct);
    private async Task UpdateBatchAsync(Guid runId,int index,string status,string items,string? error,long durationMs,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status=@status,items=CAST(@items AS jsonb),error=@error,duration_ms=@duration,updated_at=now() WHERE run_id=@runId AND batch_index=@index",connection);command.Parameters.AddWithValue("status",status);command.Parameters.AddWithValue("items",items);command.Parameters.AddWithValue("error",(object?)error??DBNull.Value);command.Parameters.AddWithValue("duration",durationMs);command.Parameters.AddWithValue("runId",runId);command.Parameters.AddWithValue("index",index);await command.ExecuteNonQueryAsync(ct);}
    public async Task<bool> BeginFinalizeAsync(Guid runId,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_runs r SET status='finalizing',updated_at=now() WHERE id=@id AND status='ready' AND NOT EXISTS(SELECT 1 FROM app_ai_checklist_batches b WHERE b.run_id=r.id AND b.status<>'completed') RETURNING 1",connection);command.Parameters.AddWithValue("id",runId);return await command.ExecuteScalarAsync(ct)is not null;}
    public async Task CompleteFinalizeAsync(Guid runId,Guid checklistId,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_runs SET status='completed',checklist_id=@checklistId,updated_at=now() WHERE id=@id",connection);command.Parameters.AddWithValue("id",runId);command.Parameters.AddWithValue("checklistId",checklistId);await command.ExecuteNonQueryAsync(ct);}
    public async Task CancelFinalizeAsync(Guid runId,string error,CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_runs SET status='ready',updated_at=now() WHERE id=@id AND status='finalizing'",connection);command.Parameters.AddWithValue("id",runId);await command.ExecuteNonQueryAsync(ct);}
    public async Task ResetInterruptedAsync(CancellationToken ct){await using var connection=await OpenAsync(ct);await using var command=new NpgsqlCommand("UPDATE app_ai_checklist_batches SET status='queued',updated_at=now() WHERE status='running'; UPDATE app_ai_checklist_runs SET status='ready',updated_at=now() WHERE status='finalizing' AND checklist_id IS NULL",connection);await command.ExecuteNonQueryAsync(ct);}
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct){var connection=new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")??throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));await connection.OpenAsync(ct);return connection;}
}
