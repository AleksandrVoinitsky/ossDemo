using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

internal sealed class PostgresAiChecklistDraftStore(IConfiguration configuration) : IAiChecklistDraftStore
{
    private readonly string connectionString = configuration.GetConnectionString("OssDatabase")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");
    private readonly string storageRoot = Path.GetFullPath(configuration["AiChecklist:OrdDirectory"]
        ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data", "ord") : "/data/ord"));

    public async Task<AiChecklistDraftState> CreateAsync(string facilitySlug, Guid? templateId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("INSERT INTO app_ai_checklist_drafts(id,facility_slug,template_id) VALUES(@id,@slug,@template)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", facilitySlug.Trim());
        AddNullableUuid(command, "template", templateId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await GetAsync(id, cancellationToken))!;
    }

    public async Task<AiChecklistDraftState?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id,facility_slug,template_id,step,version,run_id,updated_at FROM app_ai_checklist_drafts WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        AiChecklistDraftState state;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            state = new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                reader.GetInt64(4), [], reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetFieldValue<DateTimeOffset>(6));
        }
        return state with { Documents = await LoadDocumentsAsync(connection, id, cancellationToken) };
    }

    public async Task<ChecklistOperationResult<AiChecklistDraftState>> UpdateAsync(Guid id, UpdateAiChecklistDraftRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE app_ai_checklist_drafts SET facility_slug=@slug,template_id=@template,step=@step,version=version+1,updated_at=now()
            WHERE id=@id AND version=@version AND run_id IS NULL
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", request.FacilitySlug?.Trim() ?? string.Empty);
        AddNullableUuid(command, "template", request.TemplateId);
        command.Parameters.AddWithValue("step", AiChecklistDraftRules.NormalizeStep(request.Step));
        command.Parameters.AddWithValue("version", request.Version);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            var current = await GetAsync(id, cancellationToken);
            return ChecklistOperationResult<AiChecklistDraftState>.Fail(current is null ? "not_found" : "version_conflict",
                current is null ? "Черновик формирования не найден." : "Черновик уже изменён. Обновите данные.");
        }
        return ChecklistOperationResult<AiChecklistDraftState>.Success((await GetAsync(id, cancellationToken))!);
    }

    public async Task<ChecklistOperationResult<InspectionBasisDocument>> AddDocumentAsync(Guid draftId, string type, IFormFile file, CancellationToken cancellationToken)
    {
        if (!InspectionBasisDocumentType.TryParse(type, out var normalizedType))
            return ChecklistOperationResult<InspectionBasisDocument>.Fail("validation", "Выберите тип документа: приказ, распоряжение или лицензия.");
        if (!AiChecklistDraftRules.IsAllowedFile(file.FileName, file.Length))
            return ChecklistOperationResult<InspectionBasisDocument>.Fail("validation", "Допустимы PDF, Word, Excel, RTF или TXT размером до 30 МБ.");
        if (await GetAsync(draftId, cancellationToken) is not { RunId: null })
            return ChecklistOperationResult<InspectionBasisDocument>.Fail("state_conflict", "Документы завершённого черновика нельзя изменять.");

        var documentId = Guid.NewGuid();
        var directory = SafeDraftDirectory(draftId);
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var path = Path.Combine(directory, $"{documentId:N}{extension}");
        string sha256;
        try
        {
            await using var source = file.OpenReadStream();
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
            }
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                INSERT INTO app_inspection_basis_documents(id,draft_id,document_type,original_name,media_type,byte_length,sha256,storage_path)
                VALUES(@id,@draft,@type,@name,@media,@length,@sha,@path)
                RETURNING uploaded_at
                """, connection);
            command.Parameters.AddWithValue("id", documentId);
            command.Parameters.AddWithValue("draft", draftId);
            command.Parameters.AddWithValue("type", normalizedType);
            command.Parameters.AddWithValue("name", Path.GetFileName(file.FileName));
            command.Parameters.AddWithValue("media", string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
            command.Parameters.AddWithValue("length", file.Length);
            command.Parameters.AddWithValue("sha", sha256);
            command.Parameters.AddWithValue("path", path);
            var timestamp = await command.ExecuteScalarAsync(cancellationToken);
            var uploadedAt = timestamp switch
            {
                DateTimeOffset value => value,
                DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
                _ => DateTimeOffset.UtcNow
            };
            return ChecklistOperationResult<InspectionBasisDocument>.Success(new(documentId, draftId, normalizedType,
                Path.GetFileName(file.FileName), string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.Length, sha256, uploadedAt));
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task<ChecklistOperationResult<bool>> DeleteDocumentAsync(Guid draftId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM app_inspection_basis_documents WHERE id=@id AND draft_id=@draft AND run_id IS NULL RETURNING storage_path", connection, transaction);
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("draft", draftId);
        var path = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (path is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ChecklistOperationResult<bool>.Fail("not_found", "Документ не найден.");
        }
        await transaction.CommitAsync(cancellationToken);
        var safePath = Path.GetFullPath(path);
        if (safePath.StartsWith(storageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(safePath)) File.Delete(safePath);
        return ChecklistOperationResult<bool>.Success(true);
    }

    public async Task<ChecklistOperationResult<AiChecklistDraftState>> AttachRunAsync(Guid draftId, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE app_ai_checklist_drafts SET run_id=@run,step='generation',version=version+1,updated_at=now() WHERE id=@draft AND run_id IS NULL", connection, transaction))
        {
            update.Parameters.AddWithValue("run", runId);
            update.Parameters.AddWithValue("draft", draftId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChecklistOperationResult<AiChecklistDraftState>.Fail("state_conflict", "Черновик уже преобразован в запуск.");
            }
        }
        await using (var documents = new NpgsqlCommand("UPDATE app_inspection_basis_documents SET run_id=@run WHERE draft_id=@draft", connection, transaction))
        {
            documents.Parameters.AddWithValue("run", runId);
            documents.Parameters.AddWithValue("draft", draftId);
            await documents.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<AiChecklistDraftState>.Success((await GetAsync(draftId, cancellationToken))!);
    }

    private string SafeDraftDirectory(Guid draftId)
    {
        var directory = Path.GetFullPath(Path.Combine(storageRoot, draftId.ToString("N")));
        if (!directory.StartsWith(storageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Некорректный путь хранилища ОРД.");
        return directory;
    }

    private static async Task<IReadOnlyList<InspectionBasisDocument>> LoadDocumentsAsync(NpgsqlConnection connection, Guid draftId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id,draft_id,document_type,original_name,media_type,byte_length,sha256,uploaded_at FROM app_inspection_basis_documents WHERE draft_id=@draft ORDER BY uploaded_at", connection);
        command.Parameters.AddWithValue("draft", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<InspectionBasisDocument>();
        while (await reader.ReadAsync(cancellationToken)) values.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return values;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = (object?)value ?? DBNull.Value;
}
