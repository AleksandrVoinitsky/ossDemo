using Npgsql;
using System.Text.Json;

internal sealed class ChecklistDatabaseInitializer(
    IConfiguration configuration,
    ILogger<ChecklistDatabaseInitializer> logger)
{
    private const string MigrationKey = "checklists-postgres-v1";
    private const string LegacyDraftMigrationKey = "checklists-json-drafts-v1";
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private bool initialized;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")
                ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));
            await connection.OpenAsync(cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);
            await SeedAsync(connection, cancellationToken);
            await ImportLegacyDraftsAsync(connection, cancellationToken);
            initialized = true;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger.LogError(exception, "Не удалось подготовить хранилище чек-листов.");
            throw;
        }
        finally { initializationLock.Release(); }
    }

    private static async Task CreateSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_facilities (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL UNIQUE, address TEXT NOT NULL,
                nvoc_category TEXT NOT NULL, latitude NUMERIC(9,6), longitude NUMERIC(9,6), slug TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMPTZ NOT NULL DEFAULT now());
            ALTER TABLE app_facilities ALTER COLUMN latitude DROP NOT NULL;
            ALTER TABLE app_facilities ALTER COLUMN longitude DROP NOT NULL;
            ALTER TABLE app_facilities ADD COLUMN IF NOT EXISTS slug TEXT NOT NULL DEFAULT '';
            CREATE TABLE IF NOT EXISTS app_data_migrations (
                key TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS app_checklist_templates (
                id UUID PRIMARY KEY, name TEXT NOT NULL, facility_id UUID NOT NULL REFERENCES app_facilities(id),
                version BIGINT NOT NULL DEFAULT 1, created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS app_checklist_template_sections (
                id UUID PRIMARY KEY, template_id UUID NOT NULL REFERENCES app_checklist_templates(id) ON DELETE CASCADE,
                title TEXT NOT NULL, position INTEGER NOT NULL,
                UNIQUE(template_id, position));
            CREATE TABLE IF NOT EXISTS app_checklist_template_items (
                id UUID PRIMARY KEY, section_id UUID NOT NULL REFERENCES app_checklist_template_sections(id) ON DELETE CASCADE,
                title TEXT NOT NULL, basis TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', position INTEGER NOT NULL,
                UNIQUE(section_id, position));
            CREATE TABLE IF NOT EXISTS app_checklists (
                id UUID PRIMARY KEY, name TEXT NOT NULL,
                facility_id UUID REFERENCES app_facilities(id) ON DELETE SET NULL, facility_name TEXT NOT NULL,
                template_id UUID REFERENCES app_checklist_templates(id) ON DELETE SET NULL, template_name TEXT NOT NULL,
                inspection_started_on DATE, inspection_finished_on DATE,
                status TEXT NOT NULL CHECK (status IN ('draft', 'approved')),
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                approved_at TIMESTAMPTZ, approved_by TEXT, legacy_source_key TEXT UNIQUE);
            CREATE TABLE IF NOT EXISTS app_checklist_items (
                id UUID PRIMARY KEY, checklist_id UUID NOT NULL REFERENCES app_checklists(id) ON DELETE CASCADE,
                position INTEGER NOT NULL, section TEXT NOT NULL, title TEXT NOT NULL, basis TEXT NOT NULL,
                result TEXT NOT NULL DEFAULT '', nonconformity TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '',
                origin TEXT NOT NULL, UNIQUE(checklist_id, position));
            ALTER TABLE app_checklist_items ADD COLUMN IF NOT EXISTS source_label TEXT NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS ix_checklist_templates_updated ON app_checklist_templates(updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_checklists_status_approved ON app_checklists(status, approved_at DESC);
            CREATE INDEX IF NOT EXISTS ix_checklists_facility ON app_checklists(facility_id);
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@key))", connection, transaction))
        {
            migrationLock.Parameters.AddWithValue("key", MigrationKey);
            await migrationLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM app_data_migrations WHERE key=@key)", connection, transaction))
        {
            check.Parameters.AddWithValue("key", MigrationKey);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        var facilities = ChecklistSeedData.Templates.Select(item => item.Facility)
            .Concat(ChecklistSeedData.History.Select(item => item.Facility)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var facility in facilities)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO app_facilities (name, address, nvoc_category, slug)
                VALUES (@name, 'Адрес уточняется', 'I категория', @slug)
                ON CONFLICT (name) DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("name", facility);
            insert.Parameters.AddWithValue("slug", CreateSlug(facility));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var template in ChecklistSeedData.Templates)
            await InsertTemplateAsync(connection, transaction, template, cancellationToken);
        foreach (var history in ChecklistSeedData.History)
            await InsertHistoryAsync(connection, transaction, history, cancellationToken);

        await using (var record = new NpgsqlCommand("INSERT INTO app_data_migrations (key) VALUES (@key)", connection, transaction))
        {
            record.Parameters.AddWithValue("key", MigrationKey);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertTemplateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ChecklistSeedTemplate template, CancellationToken cancellationToken)
    {
        var facilityId = await GetFacilityIdAsync(connection, transaction, template.Facility, cancellationToken);
        var inserted = false;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO app_checklist_templates (id, name, facility_id)
            VALUES (@id, @name, @facilityId) ON CONFLICT (id) DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", template.Id);
            command.Parameters.AddWithValue("name", template.Name);
            command.Parameters.AddWithValue("facilityId", facilityId);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (!inserted) return;
        var sectionPosition = 0;
        foreach (var section in template.Sections)
        {
            var sectionId = Guid.NewGuid();
            await using (var command = new NpgsqlCommand("INSERT INTO app_checklist_template_sections (id, template_id, title, position) VALUES (@id,@templateId,@title,@position)", connection, transaction))
            {
                command.Parameters.AddWithValue("id", sectionId);
                command.Parameters.AddWithValue("templateId", template.Id);
                command.Parameters.AddWithValue("title", section.Title);
                command.Parameters.AddWithValue("position", ++sectionPosition);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            var itemPosition = 0;
            foreach (var item in section.Items)
            {
                await using var command = new NpgsqlCommand("INSERT INTO app_checklist_template_items (id, section_id, title, basis, note, position) VALUES (@id,@sectionId,@title,@basis,@note,@position)", connection, transaction);
                command.Parameters.AddWithValue("id", Guid.NewGuid());
                command.Parameters.AddWithValue("sectionId", sectionId);
                command.Parameters.AddWithValue("title", item.Title);
                command.Parameters.AddWithValue("basis", item.Basis);
                command.Parameters.AddWithValue("note", item.Note);
                command.Parameters.AddWithValue("position", ++itemPosition);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task InsertHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ChecklistSeedHistory history, CancellationToken cancellationToken)
    {
        var facilityId = await GetFacilityIdAsync(connection, transaction, history.Facility, cancellationToken);
        var inserted = false;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO app_checklists (id,name,facility_id,facility_name,template_name,inspection_started_on,inspection_finished_on,status,created_at,updated_at,approved_at,approved_by,legacy_source_key)
            VALUES (@id,@name,@facilityId,@facility,@templateName,@started,@finished,'approved',@approved,@approved,@approved,'legacy-import',@source)
            ON CONFLICT (id) DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", history.Id);
            command.Parameters.AddWithValue("name", history.Name);
            command.Parameters.AddWithValue("facilityId", facilityId);
            command.Parameters.AddWithValue("facility", history.Facility);
            command.Parameters.AddWithValue("templateName", "Импортированный исторический чек-лист");
            command.Parameters.AddWithValue("started", (object?)history.StartedOn ?? DBNull.Value);
            command.Parameters.AddWithValue("finished", (object?)history.FinishedOn ?? DBNull.Value);
            command.Parameters.AddWithValue("approved", ToPostgresTimestamp(history.ApprovedAt));
            command.Parameters.AddWithValue("source", history.SourceKey);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (!inserted) return;
        var position = 0;
        foreach (var item in history.Items)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO app_checklist_items (id,checklist_id,position,section,title,basis,result,nonconformity,note,origin)
                VALUES (@id,@checklistId,@position,@section,@title,@basis,@result,@nonconformity,@note,'legacy')
                """, connection, transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("checklistId", history.Id);
            command.Parameters.AddWithValue("position", ++position);
            command.Parameters.AddWithValue("section", item.Section);
            command.Parameters.AddWithValue("title", item.Title);
            command.Parameters.AddWithValue("basis", item.Basis);
            command.Parameters.AddWithValue("result", item.Result);
            command.Parameters.AddWithValue("nonconformity", item.Nonconformity);
            command.Parameters.AddWithValue("note", item.Note);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<Guid> GetFacilityIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM app_facilities WHERE name=@name", connection, transaction);
        command.Parameters.AddWithValue("name", name);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException($"Не найден объект {name}."));
    }

    private static string CreateSlug(string value) => string.Join('-', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private async Task ImportLegacyDraftsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@key))", connection, transaction))
        {
            migrationLock.Parameters.AddWithValue("key", LegacyDraftMigrationKey);
            await migrationLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM app_data_migrations WHERE key=@key)", connection, transaction))
        {
            check.Parameters.AddWithValue("key", LegacyDraftMigrationKey);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        var legacyDirectory = configuration["Checklists:Directory"] ?? "/data/checklists";
        var skippedFiles = false;
        if (Directory.Exists(legacyDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(legacyDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                LegacyWorkingChecklist? legacy;
                try
                {
                    await using var stream = File.OpenRead(path);
                    legacy = await JsonSerializer.DeserializeAsync<LegacyWorkingChecklist>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Пропущен повреждённый прежний черновик {FileName}.", Path.GetFileName(path));
                    skippedFiles = true;
                    continue;
                }
                if (legacy is null || legacy.Id == Guid.Empty || string.IsNullOrWhiteSpace(legacy.Name) || string.IsNullOrWhiteSpace(legacy.Facility))
                {
                    logger.LogWarning("Пропущен неполный прежний черновик {FileName}; импорт будет повторён при следующем запуске.", Path.GetFileName(path));
                    skippedFiles = true;
                    continue;
                }

                await using (var facility = new NpgsqlCommand("INSERT INTO app_facilities (name,address,nvoc_category,slug) VALUES (@name,'Адрес уточняется','I категория',@slug) ON CONFLICT (name) DO NOTHING", connection, transaction))
                {
                    facility.Parameters.AddWithValue("name", legacy.Facility.Trim());
                    facility.Parameters.AddWithValue("slug", CreateSlug(legacy.Facility));
                    await facility.ExecuteNonQueryAsync(cancellationToken);
                }
                var facilityId = await GetFacilityIdAsync(connection, transaction, legacy.Facility.Trim(), cancellationToken);
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO app_checklists (id,name,facility_id,facility_name,template_name,status,created_at,updated_at)
                    VALUES (@id,@name,@facilityId,@facility,'Импортированный рабочий чек-лист','draft',@created,@created)
                    ON CONFLICT (id) DO NOTHING
                    """, connection, transaction);
                insert.Parameters.AddWithValue("id", legacy.Id);
                insert.Parameters.AddWithValue("name", legacy.Name.Trim());
                insert.Parameters.AddWithValue("facilityId", facilityId);
                insert.Parameters.AddWithValue("facility", legacy.Facility.Trim());
                insert.Parameters.AddWithValue("created", legacy.CreatedAt == default ? DateTimeOffset.UtcNow : ToPostgresTimestamp(legacy.CreatedAt));
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) continue;

                var position = 0;
                foreach (var item in legacy.Items ?? [])
                {
                    if (string.IsNullOrWhiteSpace(item.Title)) continue;
                    await using var itemInsert = new NpgsqlCommand("""
                        INSERT INTO app_checklist_items (id,checklist_id,position,section,title,basis,result,nonconformity,note,origin)
                        VALUES (@id,@checklistId,@position,@section,@title,@basis,@result,@nonconformity,@note,@origin)
                        """, connection, transaction);
                    itemInsert.Parameters.AddWithValue("id", item.Id == Guid.Empty ? Guid.NewGuid() : item.Id);
                    itemInsert.Parameters.AddWithValue("checklistId", legacy.Id);
                    itemInsert.Parameters.AddWithValue("position", ++position);
                    itemInsert.Parameters.AddWithValue("section", item.Section?.Trim() ?? "Без раздела");
                    itemInsert.Parameters.AddWithValue("title", item.Title.Trim());
                    itemInsert.Parameters.AddWithValue("basis", item.Basis?.Trim() ?? "Не указано");
                    itemInsert.Parameters.AddWithValue("result", item.Result?.Trim() ?? "");
                    itemInsert.Parameters.AddWithValue("nonconformity", item.Nonconformity?.Trim() ?? "");
                    itemInsert.Parameters.AddWithValue("note", item.Note?.Trim() ?? "");
                    itemInsert.Parameters.AddWithValue("origin", item.Origin?.Trim() ?? "legacy");
                    await itemInsert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        if (!skippedFiles)
        {
            await using var record = new NpgsqlCommand("INSERT INTO app_data_migrations (key) VALUES (@key)", connection, transaction);
            record.Parameters.AddWithValue("key", LegacyDraftMigrationKey);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record LegacyWorkingChecklist(Guid Id, string? Name, string? Facility, DateTimeOffset CreatedAt, string? Status, IReadOnlyList<LegacyWorkingChecklistItem>? Items);
    private sealed record LegacyWorkingChecklistItem(Guid Id, int Number, string? Section, string? Title, string? Basis, string? Result, string? Nonconformity, string? Note, string? Origin);
    internal static DateTime ToPostgresTimestamp(DateTimeOffset value) => value.UtcDateTime;
}
