using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal sealed class PostgresClassifierRepository(IConfiguration configuration) : IClassifierRepository
{
    private readonly string connectionString = configuration.GetConnectionString("OssDatabase")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");

    public async Task<ClassifierTree> GetTreeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Guid versionId; string version; string status; DateOnly effectiveFrom;
        await using (var command = new NpgsqlCommand("SELECT id,version,status,effective_from FROM app_classifier_versions WHERE status='active' ORDER BY effective_from DESC LIMIT 1", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return ClassifierSeedData.Tree;
            versionId = reader.GetGuid(0); version = reader.GetString(1); status = reader.GetString(2); effectiveFrom = reader.GetFieldValue<DateOnly>(3);
        }
        var sections = new List<ClassifierSection>();
        await using var sectionCommand = new NpgsqlCommand("SELECT id,code,title,position FROM app_classifier_sections WHERE version_id=@version ORDER BY position,code", connection);
        sectionCommand.Parameters.AddWithValue("version", versionId);
        await using var sectionReader = await sectionCommand.ExecuteReaderAsync(cancellationToken);
        var sectionRows = new List<(Guid Id,string Code,string Title,int Position)>();
        while (await sectionReader.ReadAsync(cancellationToken)) sectionRows.Add((sectionReader.GetGuid(0),sectionReader.GetString(1),sectionReader.GetString(2),sectionReader.GetInt32(3)));
        await sectionReader.CloseAsync();
        foreach (var row in sectionRows)
        {
            var criteria = new List<ClassifierCriterion>();
            await using var criterionCommand = new NpgsqlCommand("""
                SELECT id,code,risk_text,check_text,search_terms,applicability_rules::text,source_hints::text,is_base,is_active,position
                FROM app_classifier_criteria WHERE section_id=@section ORDER BY position,code
                """, connection);
            criterionCommand.Parameters.AddWithValue("section", row.Id);
            await using var reader = await criterionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                criteria.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),
                    JsonSerializer.Deserialize<ClassifierApplicabilityRule[]>(reader.GetString(5)) ?? [],
                    JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [], reader.GetBoolean(7),reader.GetBoolean(8),reader.GetInt32(9),0,[]));
            sections.Add(new(row.Id,row.Code,row.Title,row.Position,criteria));
        }
        return new(versionId,version,status,effectiveFrom,sections);
    }

    public async Task<ChecklistOperationResult<ClassifierCriterion>> CreateCriterionAsync(ClassifierCriterionCreate request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return ChecklistOperationResult<ClassifierCriterion>.Fail("validation","Укажите код критерия.");
        var normalized = ClassifierRules.Normalize(new(request.RiskText,request.CheckText,request.SearchTerms,request.ApplicabilityRules,request.SourceHints,request.IsActive,request.Position));
        if (!normalized.IsSuccess) return ChecklistOperationResult<ClassifierCriterion>.Fail(normalized.ErrorCode!,normalized.Error!,normalized.Errors);
        var id = Guid.NewGuid();
        try
        {
            await SaveAsync(id,request.SectionId,request.Code.Trim(),normalized.Value!,false,true,cancellationToken);
            return ChecklistOperationResult<ClassifierCriterion>.Success(await FindAsync(id,cancellationToken) ?? throw new InvalidOperationException("Созданный критерий не найден."));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return ChecklistOperationResult<ClassifierCriterion>.Fail("validation","Критерий с таким кодом уже существует."); }
    }

    public async Task<ChecklistOperationResult<ClassifierCriterion>> UpdateCriterionAsync(Guid id, ClassifierCriterionWrite request, CancellationToken cancellationToken)
    {
        var normalized = ClassifierRules.Normalize(request);
        if (!normalized.IsSuccess) return ChecklistOperationResult<ClassifierCriterion>.Fail(normalized.ErrorCode!,normalized.Error!,normalized.Errors);
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE app_classifier_criteria SET risk_text=@risk,check_text=@check,search_terms=@terms,applicability_rules=@rules,source_hints=@hints,is_active=@active,position=@position,updated_at=now() WHERE id=@id
            """,connection);
        AddWriteParameters(command,normalized.Value!); command.Parameters.AddWithValue("id",id);
        if (await command.ExecuteNonQueryAsync(cancellationToken)==0) return ChecklistOperationResult<ClassifierCriterion>.Fail("not_found","Критерий не найден.");
        return ChecklistOperationResult<ClassifierCriterion>.Success(await FindAsync(id,cancellationToken) ?? throw new InvalidOperationException("Изменённый критерий не найден."));
    }

    public async Task<bool> DeleteCriterionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE app_classifier_criteria SET is_active=false,updated_at=now() WHERE id=@id",connection);
        command.Parameters.AddWithValue("id",id); return await command.ExecuteNonQueryAsync(cancellationToken)>0;
    }

    private async Task SaveAsync(Guid id, Guid sectionId, string code, ClassifierCriterionWrite value, bool isBase, bool insert, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_classifier_criteria(id,section_id,code,risk_text,check_text,search_terms,applicability_rules,source_hints,is_base,is_active,position)
            VALUES(@id,@section,@code,@risk,@check,@terms,@rules,@hints,@base,@active,@position)
            """,connection);
        command.Parameters.AddWithValue("id",id); command.Parameters.AddWithValue("section",sectionId); command.Parameters.AddWithValue("code",code); command.Parameters.AddWithValue("base",isBase);
        AddWriteParameters(command,value); await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<ClassifierCriterion?> FindAsync(Guid id, CancellationToken ct)
    {
        var tree=await GetTreeAsync(ct); return tree.Sections.SelectMany(x=>x.Criteria).FirstOrDefault(x=>x.Id==id);
    }

    private static void AddWriteParameters(NpgsqlCommand command, ClassifierCriterionWrite value)
    {
        command.Parameters.AddWithValue("risk",value.RiskText!); command.Parameters.AddWithValue("check",value.CheckText!);
        command.Parameters.AddWithValue("terms",value.SearchTerms ?? ""); command.Parameters.AddWithValue("rules",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(value.ApplicabilityRules));
        command.Parameters.AddWithValue("hints",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(value.SourceHints)); command.Parameters.AddWithValue("active",value.IsActive); command.Parameters.AddWithValue("position",value.Position);
    }
}
