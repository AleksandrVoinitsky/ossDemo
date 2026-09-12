using Npgsql;
using NpgsqlTypes;

internal sealed class PostgresChecklistRepository(IConfiguration configuration) : IChecklistRepository
{
    public async Task<IReadOnlyList<ChecklistTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT t.id,t.name,t.facility_id,f.name,t.version,t.updated_at,
                   count(DISTINCT s.id)::int,count(i.id)::int
            FROM app_checklist_templates t JOIN app_facilities f ON f.id=t.facility_id
            LEFT JOIN app_checklist_template_sections s ON s.template_id=t.id
            LEFT JOIN app_checklist_template_items i ON i.section_id=s.id
            GROUP BY t.id,f.name ORDER BY t.updated_at DESC,t.name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<ChecklistTemplateSummary>();
        while (await reader.ReadAsync(cancellationToken))
            values.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetGuid(2),reader.GetString(3),reader.GetInt64(4),reader.GetInt32(6),reader.GetInt32(7),reader.GetFieldValue<DateTimeOffset>(5)));
        return values;
    }

    public async Task<ChecklistTemplateDetails?> GetTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await GetTemplateAsync(connection, null, id, cancellationToken);
    }

    public async Task<ChecklistTemplateDetails> CreateTemplateAsync(ChecklistTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        await using (var command = new NpgsqlCommand("INSERT INTO app_checklist_templates (id,name,facility_id) VALUES (@id,@name,@facilityId)", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("name", request.Name!); command.Parameters.AddWithValue("facilityId", request.FacilityId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertSectionsAsync(connection, transaction, id, request.Sections, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetTemplateAsync(id, cancellationToken))!;
    }

    public async Task<ChecklistOperationResult<ChecklistTemplateDetails>> UpdateTemplateAsync(Guid id, ChecklistTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new NpgsqlCommand("UPDATE app_checklist_templates SET name=@name,facility_id=@facilityId,version=version+1,updated_at=now() WHERE id=@id AND version=@version", connection, transaction);
        update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("name", request.Name!); update.Parameters.AddWithValue("facilityId", request.FacilityId); update.Parameters.AddWithValue("version", request.Version);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM app_checklist_templates WHERE id=@id)", connection, transaction);
            exists.Parameters.AddWithValue("id", id);
            var found = (bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false);
            await transaction.RollbackAsync(cancellationToken);
            return ChecklistOperationResult<ChecklistTemplateDetails>.Fail(found ? "version_conflict" : "not_found", found ? "Шаблон уже изменён. Обновите страницу." : "Шаблон не найден.");
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM app_checklist_template_sections WHERE template_id=@id", connection, transaction)) { delete.Parameters.AddWithValue("id", id); await delete.ExecuteNonQueryAsync(cancellationToken); }
        await InsertSectionsAsync(connection, transaction, id, request.Sections, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistTemplateDetails>.Success((await GetTemplateAsync(id, cancellationToken))!);
    }

    public async Task<ChecklistOperationResult<ChecklistTemplateDetails>> CopyTemplateAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockSource = new NpgsqlCommand("SELECT 1 FROM app_checklist_templates WHERE id=@id FOR SHARE", connection, transaction))
        {
            lockSource.Parameters.AddWithValue("id", id);
            if (await lockSource.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChecklistOperationResult<ChecklistTemplateDetails>.Fail("not_found", "Шаблон не найден.");
            }
        }

        var source = (await GetTemplateAsync(connection, transaction, id, cancellationToken))!;
        var copyId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("INSERT INTO app_checklist_templates (id,name,facility_id) VALUES (@id,@name,@facilityId)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", copyId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("facilityId", source.FacilityId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var sections = source.Sections.Select(section => new ChecklistTemplateSectionWrite(section.Title, section.Position,
            section.Items.Select(item => new ChecklistTemplateItemWrite(item.Title, item.Basis, item.Note, item.Position)).ToArray())).ToArray();
        await InsertSectionsAsync(connection, transaction, copyId, sections, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistTemplateDetails>.Success((await GetTemplateAsync(copyId, cancellationToken))!);
    }

    public async Task<bool> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM app_checklist_templates WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ChecklistOperationResult<ChecklistDetails>> CreateDraftAsync(CreateChecklistRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string templateName;
        string facilityName;
        await using (var command = new NpgsqlCommand("SELECT t.name,f.name FROM app_checklist_templates t JOIN app_facilities f ON f.id=t.facility_id WHERE t.id=@templateId AND t.facility_id=@facilityId FOR SHARE", connection, transaction))
        {
            command.Parameters.AddWithValue("templateId", request.TemplateId); command.Parameters.AddWithValue("facilityId", request.FacilityId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) { await transaction.RollbackAsync(cancellationToken); return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Шаблон или объект не найден."); }
            templateName=reader.GetString(0); facilityName=reader.GetString(1);
        }
        var id=Guid.NewGuid(); var name=string.IsNullOrWhiteSpace(request.Name) ? $"Чек-лист ООС — {facilityName}" : request.Name!;
        await using (var insert = new NpgsqlCommand("INSERT INTO app_checklists (id,name,facility_id,facility_name,template_id,template_name,inspection_started_on,inspection_finished_on,status) VALUES (@id,@name,@facilityId,@facility,@templateId,@templateName,@started,@finished,'draft')", connection, transaction))
        {
            insert.Parameters.AddWithValue("id",id); insert.Parameters.AddWithValue("name",name); insert.Parameters.AddWithValue("facilityId",request.FacilityId); insert.Parameters.AddWithValue("facility",facilityName); insert.Parameters.AddWithValue("templateId",request.TemplateId); insert.Parameters.AddWithValue("templateName",templateName);
            AddDate(insert,"started",request.InspectionStartedOn); AddDate(insert,"finished",request.InspectionFinishedOn); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var copy = new NpgsqlCommand("""
            INSERT INTO app_checklist_items (id,checklist_id,position,section,title,basis,note,origin)
            SELECT gen_random_uuid(),@checklistId,row_number() OVER (ORDER BY s.position,i.position),s.title,i.title,i.basis,i.note,'template'
            FROM app_checklist_template_sections s JOIN app_checklist_template_items i ON i.section_id=s.id
            WHERE s.template_id=@templateId ORDER BY s.position,i.position
            """, connection, transaction))
        { copy.Parameters.AddWithValue("checklistId",id); copy.Parameters.AddWithValue("templateId",request.TemplateId); await copy.ExecuteNonQueryAsync(cancellationToken); }
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistDetails>.Success((await GetChecklistAsync(id,cancellationToken))!);
    }

    public async Task<ChecklistOperationResult<ChecklistDetails>> CreateAiDraftAsync(CreateAiChecklistDraftRequest request, CancellationToken cancellationToken)
    {
        if (request.FacilityId == Guid.Empty || request.Items.Count == 0)
            return ChecklistOperationResult<ChecklistDetails>.Fail("validation", "ИИ не сформировал подтверждённые пункты чек-листа.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var facility = new NpgsqlCommand("SELECT name FROM app_facilities WHERE id=@id FOR SHARE", connection, transaction))
        {
            facility.Parameters.AddWithValue("id", request.FacilityId);
            if (await facility.ExecuteScalarAsync(cancellationToken) is not string facilityName)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Объект проверки не найден.");
            }
            if (!string.Equals(facilityName, request.FacilityName, StringComparison.Ordinal))
                request = request with { FacilityName = facilityName };
        }

        var id = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"ИИ-чек-лист — {request.FacilityName}" : request.Name.Trim();
        await using (var insert = new NpgsqlCommand("INSERT INTO app_checklists (id,name,facility_id,facility_name,template_id,template_name,status,ai_run_id) VALUES (@id,@name,@facilityId,@facility,NULL,@templateName,'draft',@runId) ON CONFLICT (ai_run_id) WHERE ai_run_id IS NOT NULL DO NOTHING", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("facilityId", request.FacilityId);
            insert.Parameters.AddWithValue("facility", request.FacilityName);
            insert.Parameters.AddWithValue("templateName", "ИИ · карточка объекта");
            AddNullableUuid(insert, "runId", request.RunId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0 && request.RunId is { } existingRunId)
            {
                await transaction.RollbackAsync(cancellationToken);
                await using var existingConnection = await OpenAsync(cancellationToken);
                await using var existing = new NpgsqlCommand("SELECT id FROM app_checklists WHERE ai_run_id=@runId", existingConnection);
                existing.Parameters.AddWithValue("runId", existingRunId);
                var existingId = (Guid)(await existing.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Не найден идемпотентный ИИ-чек-лист."));
                return ChecklistOperationResult<ChecklistDetails>.Success((await GetChecklistAsync(existingId, cancellationToken))!);
            }
        }

        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            await using var insertItem = new NpgsqlCommand("INSERT INTO app_checklist_items (id,checklist_id,position,section,title,basis,note,origin) VALUES (@id,@checklistId,@position,@section,@title,@basis,@note,'ai')", connection, transaction);
            insertItem.Parameters.AddWithValue("id", Guid.NewGuid());
            insertItem.Parameters.AddWithValue("checklistId", id);
            insertItem.Parameters.AddWithValue("position", index + 1);
            insertItem.Parameters.AddWithValue("section", item.Section);
            insertItem.Parameters.AddWithValue("title", item.Title);
            insertItem.Parameters.AddWithValue("basis", item.Basis);
            insertItem.Parameters.AddWithValue("note", item.Note);
            await insertItem.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistDetails>.Success((await GetChecklistAsync(id, cancellationToken))!);
    }

    public async Task<ChecklistDetails?> GetChecklistAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id,name,facility_id,facility_name,template_id,template_name,status,inspection_started_on,inspection_finished_on,created_at,updated_at,approved_at,approved_by FROM app_checklists WHERE id=@id", connection);
        command.Parameters.AddWithValue("id",id);
        ChecklistDetails header;
        await using (var reader=await command.ExecuteReaderAsync(cancellationToken))
        { if(!await reader.ReadAsync(cancellationToken)) return null; header=new(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetGuid(4),reader.GetString(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetFieldValue<DateOnly>(7),reader.IsDBNull(8)?null:reader.GetFieldValue<DateOnly>(8),reader.GetFieldValue<DateTimeOffset>(9),reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11),reader.IsDBNull(12)?null:reader.GetString(12),[]); }
        await using var itemsCommand=new NpgsqlCommand("SELECT id,position,section,title,basis,result,nonconformity,note,origin FROM app_checklist_items WHERE checklist_id=@id ORDER BY position",connection); itemsCommand.Parameters.AddWithValue("id",id);
        await using var itemsReader=await itemsCommand.ExecuteReaderAsync(cancellationToken); var items=new List<ChecklistItemDetails>();
        while(await itemsReader.ReadAsync(cancellationToken)) items.Add(new(itemsReader.GetGuid(0),itemsReader.GetInt32(1),itemsReader.GetString(2),itemsReader.GetString(3),itemsReader.GetString(4),itemsReader.GetString(5),itemsReader.GetString(6),itemsReader.GetString(7),itemsReader.GetString(8)));
        return header with { Items=items };
    }

    public Task<IReadOnlyList<ChecklistSummary>> ListDraftsAsync(CancellationToken cancellationToken) => ListAsync(new ChecklistHistoryFilter(null,null,null,null),"draft",cancellationToken);
    public Task<IReadOnlyList<ChecklistSummary>> ListHistoryAsync(ChecklistHistoryFilter filter, CancellationToken cancellationToken) => ListAsync(filter,"approved",cancellationToken);

    public async Task<ChecklistOperationResult<ChecklistDetails>> AddDraftItemAsync(Guid id, AddChecklistItemRequest request, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var transaction=await connection.BeginTransactionAsync(cancellationToken);
        await using var status=new NpgsqlCommand("SELECT status FROM app_checklists WHERE id=@id FOR UPDATE",connection,transaction); status.Parameters.AddWithValue("id",id); var value=await status.ExecuteScalarAsync(cancellationToken);
        if(value is null){await transaction.RollbackAsync(cancellationToken);return ChecklistOperationResult<ChecklistDetails>.Fail("not_found","Чек-лист не найден.");}
        if((string)value!="draft"){await transaction.RollbackAsync(cancellationToken);return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict","Утверждённый чек-лист нельзя изменять.");}
        await using var insert=new NpgsqlCommand("INSERT INTO app_checklist_items (id,checklist_id,position,section,title,basis,note,origin) SELECT @itemId,@id,coalesce(max(position),0)+1,@section,@title,@basis,@note,'manual' FROM app_checklist_items WHERE checklist_id=@id",connection,transaction);
        insert.Parameters.AddWithValue("itemId",Guid.NewGuid());insert.Parameters.AddWithValue("id",id);insert.Parameters.AddWithValue("section",request.Section??"Ручной пункт");insert.Parameters.AddWithValue("title",request.Title!);insert.Parameters.AddWithValue("basis",request.Basis!);insert.Parameters.AddWithValue("note",request.Note??"");await insert.ExecuteNonQueryAsync(cancellationToken);
        await using(var touch=new NpgsqlCommand("UPDATE app_checklists SET updated_at=now() WHERE id=@id",connection,transaction)){touch.Parameters.AddWithValue("id",id);await touch.ExecuteNonQueryAsync(cancellationToken);} await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistDetails>.Success((await GetChecklistAsync(id,cancellationToken))!);
    }

    public async Task<ChecklistOperationResult<ChecklistDetails>> UpdateDraftItemAsync(Guid id, Guid itemId, UpdateChecklistItemRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var status = new NpgsqlCommand("SELECT status FROM app_checklists WHERE id=@id FOR UPDATE", connection, transaction);
        status.Parameters.AddWithValue("id", id);
        var value = await status.ExecuteScalarAsync(cancellationToken);
        if (value is null) { await transaction.RollbackAsync(cancellationToken); return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Чек-лист не найден."); }
        if ((string)value != "draft") { await transaction.RollbackAsync(cancellationToken); return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Утверждённый чек-лист нельзя изменять."); }
        await using var update = new NpgsqlCommand("UPDATE app_checklist_items SET result=@result,nonconformity=@nonconformity,note=@note WHERE id=@itemId AND checklist_id=@id", connection, transaction);
        update.Parameters.AddWithValue("id", id);
        update.Parameters.AddWithValue("itemId", itemId);
        update.Parameters.AddWithValue("result", request.Result!);
        update.Parameters.AddWithValue("nonconformity", request.Nonconformity ?? "");
        update.Parameters.AddWithValue("note", request.Note ?? "");
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Пункт чек-листа не найден.");
        }
        await using (var touch = new NpgsqlCommand("UPDATE app_checklists SET updated_at=now() WHERE id=@id", connection, transaction))
        {
            touch.Parameters.AddWithValue("id", id);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistDetails>.Success((await GetChecklistAsync(id, cancellationToken))!);
    }

    public async Task<ChecklistOperationResult<ChecklistDetails>> ApproveAsync(Guid id, string approvedBy, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockChecklist = new NpgsqlCommand("SELECT status FROM app_checklists WHERE id=@id FOR UPDATE", connection, transaction))
        {
            lockChecklist.Parameters.AddWithValue("id", id);
            var status = await lockChecklist.ExecuteScalarAsync(cancellationToken);
            if (status is null) { await transaction.RollbackAsync(cancellationToken); return ChecklistOperationResult<ChecklistDetails>.Fail("not_found", "Чек-лист не найден."); }
            if ((string)status != "draft") { await transaction.RollbackAsync(cancellationToken); return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Чек-лист уже утверждён."); }
        }
        await using (var incomplete = new NpgsqlCommand("SELECT count(*) FILTER (WHERE btrim(result)=''),count(*) FROM app_checklist_items WHERE checklist_id=@id", connection, transaction))
        {
            incomplete.Parameters.AddWithValue("id", id);
            await using var reader = await incomplete.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(1) == 0 || reader.GetInt64(0) > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChecklistOperationResult<ChecklistDetails>.Fail("state_conflict", "Заполните результаты всех пунктов перед утверждением.");
            }
        }
        await using var command=new NpgsqlCommand("UPDATE app_checklists SET status='approved',approved_at=now(),approved_by=@approvedBy,updated_at=now() WHERE id=@id",connection,transaction);command.Parameters.AddWithValue("id",id);command.Parameters.AddWithValue("approvedBy",approvedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ChecklistOperationResult<ChecklistDetails>.Success((await GetChecklistAsync(id,cancellationToken))!);
    }

    private async Task<IReadOnlyList<ChecklistSummary>> ListAsync(ChecklistHistoryFilter filter,string status,CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var command=new NpgsqlCommand("""
            SELECT c.id,c.name,c.facility_id,c.facility_name,c.template_name,c.status,c.inspection_started_on,c.inspection_finished_on,c.created_at,c.updated_at,c.approved_at,c.approved_by,count(i.id)::int
            FROM app_checklists c LEFT JOIN app_checklist_items i ON i.checklist_id=c.id
            WHERE c.status=@status AND (@search='' OR c.name ILIKE '%'||@search||'%' OR c.facility_name ILIKE '%'||@search||'%')
              AND (@facilityId IS NULL OR c.facility_id=@facilityId) AND (@from IS NULL OR c.approved_at::date>=@from) AND (@to IS NULL OR c.approved_at::date<=@to)
            GROUP BY c.id ORDER BY coalesce(c.approved_at,c.updated_at) DESC
            """,connection);command.Parameters.AddWithValue("status",status);command.Parameters.AddWithValue("search",filter.Search?.Trim()??"");AddNullableUuid(command,"facilityId",filter.FacilityId);AddDate(command,"from",filter.From);AddDate(command,"to",filter.To);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var values=new List<ChecklistSummary>();while(await reader.ReadAsync(cancellationToken)) values.Add(new(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateOnly>(6),reader.IsDBNull(7)?null:reader.GetFieldValue<DateOnly>(7),reader.GetFieldValue<DateTimeOffset>(8),reader.GetFieldValue<DateTimeOffset>(9),reader.IsDBNull(10)?null:reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.GetInt32(12)));return values;
    }

    private static async Task<ChecklistTemplateDetails?> GetTemplateAsync(NpgsqlConnection connection,NpgsqlTransaction? transaction,Guid id,CancellationToken cancellationToken)
    {
        await using var command=new NpgsqlCommand("SELECT t.id,t.name,t.facility_id,f.name,t.version,t.created_at,t.updated_at FROM app_checklist_templates t JOIN app_facilities f ON f.id=t.facility_id WHERE t.id=@id",connection,transaction);command.Parameters.AddWithValue("id",id);ChecklistTemplateDetails header;
        await using(var reader=await command.ExecuteReaderAsync(cancellationToken)){if(!await reader.ReadAsync(cancellationToken))return null;header=new(reader.GetGuid(0),reader.GetString(1),reader.GetGuid(2),reader.GetString(3),reader.GetInt64(4),reader.GetFieldValue<DateTimeOffset>(5),reader.GetFieldValue<DateTimeOffset>(6),[]);}
        await using var child=new NpgsqlCommand("SELECT s.id,s.title,s.position,i.id,i.title,i.basis,i.note,i.position FROM app_checklist_template_sections s LEFT JOIN app_checklist_template_items i ON i.section_id=s.id WHERE s.template_id=@id ORDER BY s.position,i.position",connection,transaction);child.Parameters.AddWithValue("id",id);await using var childReader=await child.ExecuteReaderAsync(cancellationToken);var sections=new List<ChecklistTemplateSectionDetails>();Guid? current=null;List<ChecklistTemplateItemDetails>? items=null;while(await childReader.ReadAsync(cancellationToken)){var sectionId=childReader.GetGuid(0);if(current!=sectionId){items=[];sections.Add(new(sectionId,childReader.GetString(1),childReader.GetInt32(2),items));current=sectionId;}if(!childReader.IsDBNull(3))items!.Add(new(childReader.GetGuid(3),childReader.GetString(4),childReader.GetString(5),childReader.GetString(6),childReader.GetInt32(7)));}return header with{Sections=sections};
    }

    private static async Task InsertSectionsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid templateId,IReadOnlyList<ChecklistTemplateSectionWrite> sections,CancellationToken cancellationToken)
    {foreach(var section in sections){var sectionId=Guid.NewGuid();await using(var command=new NpgsqlCommand("INSERT INTO app_checklist_template_sections (id,template_id,title,position) VALUES (@id,@templateId,@title,@position)",connection,transaction)){command.Parameters.AddWithValue("id",sectionId);command.Parameters.AddWithValue("templateId",templateId);command.Parameters.AddWithValue("title",section.Title!);command.Parameters.AddWithValue("position",section.Position);await command.ExecuteNonQueryAsync(cancellationToken);}foreach(var item in section.Items){await using var command=new NpgsqlCommand("INSERT INTO app_checklist_template_items (id,section_id,title,basis,note,position) VALUES (@id,@sectionId,@title,@basis,@note,@position)",connection,transaction);command.Parameters.AddWithValue("id",Guid.NewGuid());command.Parameters.AddWithValue("sectionId",sectionId);command.Parameters.AddWithValue("title",item.Title!);command.Parameters.AddWithValue("basis",item.Basis!);command.Parameters.AddWithValue("note",item.Note??"");command.Parameters.AddWithValue("position",item.Position);await command.ExecuteNonQueryAsync(cancellationToken);}}}
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken){var connection=new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")??throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));await connection.OpenAsync(cancellationToken);return connection;}
    private static void AddDate(NpgsqlCommand command,string name,DateOnly? value)=>command.Parameters.Add(name,NpgsqlDbType.Date).Value=(object?)value??DBNull.Value;
    private static void AddNullableUuid(NpgsqlCommand command,string name,Guid? value)=>command.Parameters.Add(name,NpgsqlDbType.Uuid).Value=(object?)value??DBNull.Value;
}
