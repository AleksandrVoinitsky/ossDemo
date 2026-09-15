using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal sealed class PostgresRequirementWorkspace(
    IConfiguration configuration,
    FacilityChecklistCatalogs catalogs) : IRequirementWorkspace
{
    private readonly string connectionString = configuration.GetConnectionString("OssDatabase")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");

    public async Task<ResolvedCriterionRequirements?> ResolveCriterionAsync(Guid criterionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var criterionCode = await GetCriterionCodeAsync(connection, criterionId, cancellationToken);
        if (criterionCode is null) return null;
        return await ResolveAsync(connection, criterionId, criterionCode, cancellationToken);
    }

    public async Task<IReadOnlyList<ResolvedRequirement>> SearchAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var revisions = await LoadRevisionsAsync(connection, cancellationToken);
        var normalized = query?.Trim() ?? string.Empty;
        return catalogs.Requirements.Items
            .Select(item => ToResolved(item, revisions.GetValueOrDefault(item.Id), "catalog"))
            .Where(item => normalized.Length == 0
                || item.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || item.Basis.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || item.Requirement.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 100))
            .ToArray();
    }

    public async Task<ChecklistOperationResult<ResolvedRequirement>> SaveRevisionAsync(
        string requirementId,
        RequirementRevisionWrite request,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = RequirementWorkspaceRules.NormalizeRevision(request);
        if (!normalized.IsSuccess)
            return ChecklistOperationResult<ResolvedRequirement>.Fail(normalized.ErrorCode!, normalized.Error!, normalized.Errors);
        var source = catalogs.Requirements.Find(requirementId);
        if (source is null)
            return ChecklistOperationResult<ResolvedRequirement>.Fail("not_found", "Требование не найдено.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        RequirementRevision? before = null;
        await using (var read = new NpgsqlCommand("SELECT requirement_text,basis,version FROM app_requirement_revisions WHERE requirement_id=@id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("id", source.Id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                before = new(source.Id, reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
        }

        var currentVersion = before?.Version ?? 1;
        if (currentVersion != normalized.Value!.Version)
            return ChecklistOperationResult<ResolvedRequirement>.Fail("version_conflict", "Требование уже изменено другим пользователем.");
        var revision = new RequirementRevision(source.Id, normalized.Value.Requirement!, normalized.Value.Basis!, currentVersion + 1);
        await using (var write = new NpgsqlCommand("""
            INSERT INTO app_requirement_revisions(requirement_id,requirement_text,basis,version,updated_by)
            VALUES(@id,@requirement,@basis,@version,@actor)
            ON CONFLICT(requirement_id) DO UPDATE SET
                requirement_text=EXCLUDED.requirement_text,basis=EXCLUDED.basis,version=EXCLUDED.version,
                updated_by=EXCLUDED.updated_by,updated_at=now()
            """, connection, transaction))
        {
            write.Parameters.AddWithValue("id", revision.RequirementId);
            write.Parameters.AddWithValue("requirement", revision.Requirement);
            write.Parameters.AddWithValue("basis", revision.Basis);
            write.Parameters.AddWithValue("version", revision.Version);
            write.Parameters.AddWithValue("actor", NormalizeActor(actor));
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteAuditAsync(connection, transaction, "revision", null, source.Id, "save", before, revision, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ResolvedRequirement>.Success(ToResolved(source, revision, "catalog"));
    }

    public Task<ChecklistOperationResult<ResolvedCriterionRequirements>> SetLinkAsync(
        Guid criterionId, string requirementId, string? action, string actor, CancellationToken cancellationToken) =>
        ChangeLinkAsync(criterionId, requirementId, RequirementWorkspaceRules.NormalizeLinkAction(action), actor, false, cancellationToken);

    public Task<ChecklistOperationResult<ResolvedCriterionRequirements>> ClearLinkAsync(
        Guid criterionId, string requirementId, string actor, CancellationToken cancellationToken) =>
        ChangeLinkAsync(criterionId, requirementId, null, actor, true, cancellationToken);

    private async Task<ChecklistOperationResult<ResolvedCriterionRequirements>> ChangeLinkAsync(
        Guid criterionId,
        string requirementId,
        string? action,
        string actor,
        bool clear,
        CancellationToken cancellationToken)
    {
        if (!clear && action is null)
            return ChecklistOperationResult<ResolvedCriterionRequirements>.Fail("validation", "Действие связи должно быть include или exclude.");
        if (!catalogs.Requirements.ContainsId(requirementId))
            return ChecklistOperationResult<ResolvedCriterionRequirements>.Fail("not_found", "Требование не найдено.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var criterionCode = await GetCriterionCodeAsync(connection, criterionId, cancellationToken, transaction);
        if (criterionCode is null)
            return ChecklistOperationResult<ResolvedCriterionRequirements>.Fail("not_found", "Критерий не найден.");

        RequirementLinkOverride? before = null;
        await using (var read = new NpgsqlCommand("SELECT action,version FROM app_classifier_requirement_links WHERE criterion_id=@criterion AND requirement_id=@requirement FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("criterion", criterionId);
            read.Parameters.AddWithValue("requirement", requirementId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) before = new(requirementId, reader.GetString(0), reader.GetInt64(1));
        }

        RequirementLinkOverride? after = null;
        if (clear)
        {
            await using var delete = new NpgsqlCommand("DELETE FROM app_classifier_requirement_links WHERE criterion_id=@criterion AND requirement_id=@requirement", connection, transaction);
            delete.Parameters.AddWithValue("criterion", criterionId);
            delete.Parameters.AddWithValue("requirement", requirementId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            after = new(requirementId, action!, (before?.Version ?? 0) + 1);
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO app_classifier_requirement_links(criterion_id,requirement_id,action,version,updated_by)
                VALUES(@criterion,@requirement,@action,@version,@actor)
                ON CONFLICT(criterion_id,requirement_id) DO UPDATE SET
                    action=EXCLUDED.action,version=EXCLUDED.version,updated_by=EXCLUDED.updated_by,updated_at=now()
                """, connection, transaction);
            upsert.Parameters.AddWithValue("criterion", criterionId);
            upsert.Parameters.AddWithValue("requirement", requirementId);
            upsert.Parameters.AddWithValue("action", action!);
            upsert.Parameters.AddWithValue("version", after.Version);
            upsert.Parameters.AddWithValue("actor", NormalizeActor(actor));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteAuditAsync(connection, transaction, "link", criterionId, requirementId, clear ? "restore" : action!, before, after, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var resolved = await ResolveCriterionAsync(criterionId, cancellationToken);
        return ChecklistOperationResult<ResolvedCriterionRequirements>.Success(resolved!);
    }

    private async Task<ResolvedCriterionRequirements> ResolveAsync(NpgsqlConnection connection, Guid criterionId, string criterionCode, CancellationToken cancellationToken)
    {
        var links = new List<RequirementLinkOverride>();
        await using (var command = new NpgsqlCommand("SELECT requirement_id,action,version FROM app_classifier_requirement_links WHERE criterion_id=@criterion", connection))
        {
            command.Parameters.AddWithValue("criterion", criterionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) links.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        var revisions = await LoadRevisionsAsync(connection, cancellationToken);
        return RequirementWorkspaceResolver.Resolve(catalogs.Requirements.Items, criterionCode, links, revisions.Values.ToArray());
    }

    private static ResolvedRequirement ToResolved(RequirementCatalogItem source, RequirementRevision? revision, string linkSource) => new(
        source.Id, source.Levels, source.Groups, source.ClassifierCodes,
        revision?.Basis ?? source.Basis, revision?.Requirement ?? source.Requirement, source.Categories,
        linkSource, revision is not null, revision?.Version ?? 1);

    private static async Task<Dictionary<string, RequirementRevision>> LoadRevisionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var revisions = new Dictionary<string, RequirementRevision>(StringComparer.OrdinalIgnoreCase);
        await using var command = new NpgsqlCommand("SELECT requirement_id,requirement_text,basis,version FROM app_requirement_revisions", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            revisions[reader.GetString(0)] = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
        return revisions;
    }

    private static async Task<string?> GetCriterionCodeAsync(
        NpgsqlConnection connection,
        Guid criterionId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("SELECT code FROM app_classifier_criteria WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", criterionId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityType,
        Guid? criterionId,
        string requirementId,
        string action,
        object? before,
        object? after,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_requirement_change_log(id,entity_type,criterion_id,requirement_id,action,before_value,after_value,actor)
            VALUES(@id,@entity,@criterion,@requirement,@action,@before,@after,@actor)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("entity", entityType);
        command.Parameters.Add(new NpgsqlParameter("criterion", NpgsqlDbType.Uuid)
        {
            Value = criterionId is null ? DBNull.Value : criterionId.Value
        });
        command.Parameters.AddWithValue("requirement", requirementId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb) { Value = before is null ? DBNull.Value : JsonSerializer.Serialize(before) });
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb) { Value = after is null ? DBNull.Value : JsonSerializer.Serialize(after) });
        command.Parameters.AddWithValue("actor", NormalizeActor(actor));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeActor(string actor) => string.IsNullOrWhiteSpace(actor) ? "Инспектор" : actor.Trim();
}
