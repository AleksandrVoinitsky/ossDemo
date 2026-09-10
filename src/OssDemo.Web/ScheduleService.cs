using Npgsql;

public sealed class ScheduleService(IConfiguration configuration, OperationalDataService operationalData)
{
    public async Task<IReadOnlyList<ScheduleItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured) return Array.Empty<ScheduleItem>();
        await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, title, event_type, status, facility_slug, facility_name, responsible, start_date, end_date, criteria, readiness, note FROM app_schedule_events ORDER BY start_date, title", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ScheduleItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadItem(reader));
        return items;
    }

    public async Task<ScheduleItem?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured) return null;
        await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, title, event_type, status, facility_slug, facility_name, responsible, start_date, end_date, criteria, readiness, note FROM app_schedule_events WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<ScheduleItem?> SaveAsync(Guid? id, ScheduleItemRequest request, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured || !IsValid(request)) return null;
        await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var facilityCommand = new NpgsqlCommand("SELECT slug, name FROM app_facilities WHERE slug = @slug", connection);
        facilityCommand.Parameters.AddWithValue("slug", request.FacilitySlug.Trim());
        await using var facilityReader = await facilityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await facilityReader.ReadAsync(cancellationToken)) return null;
        var facilitySlug = facilityReader.GetString(0);
        var facilityName = facilityReader.GetString(1);
        await facilityReader.CloseAsync();

        var eventId = id ?? Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_schedule_events (id, title, event_type, status, facility_slug, facility_name, responsible, start_date, end_date, criteria, readiness, note)
            VALUES (@id, @title, @eventType, @status, @facilitySlug, @facilityName, @responsible, @startDate, @endDate, @criteria, @readiness, @note)
            ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, event_type = EXCLUDED.event_type, status = EXCLUDED.status,
                facility_slug = EXCLUDED.facility_slug, facility_name = EXCLUDED.facility_name, responsible = EXCLUDED.responsible,
                start_date = EXCLUDED.start_date, end_date = EXCLUDED.end_date, criteria = EXCLUDED.criteria, readiness = EXCLUDED.readiness,
                note = EXCLUDED.note, updated_at = now()
            """, connection);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("title", request.Title.Trim());
        command.Parameters.AddWithValue("eventType", request.EventType.Trim());
        command.Parameters.AddWithValue("status", request.Status.Trim());
        command.Parameters.AddWithValue("facilitySlug", facilitySlug);
        command.Parameters.AddWithValue("facilityName", facilityName);
        command.Parameters.AddWithValue("responsible", request.Responsible.Trim());
        command.Parameters.AddWithValue("startDate", request.StartDate);
        command.Parameters.AddWithValue("endDate", request.EndDate);
        command.Parameters.AddWithValue("criteria", request.Criteria?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("readiness", request.Readiness?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("note", request.Note?.Trim() ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetAsync(eventId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured) return false;
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM app_schedule_events WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_schedule_events (
                id UUID PRIMARY KEY, title TEXT NOT NULL, event_type TEXT NOT NULL, status TEXT NOT NULL,
                facility_slug TEXT NOT NULL, facility_name TEXT NOT NULL, responsible TEXT NOT NULL,
                start_date DATE NOT NULL, end_date DATE NOT NULL, criteria TEXT NOT NULL DEFAULT '',
                readiness TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '', updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                CONSTRAINT app_schedule_events_dates_valid CHECK (end_date >= start_date));
            INSERT INTO app_schedule_events (id, title, event_type, status, facility_slug, facility_name, responsible, start_date, end_date, criteria, readiness, note)
            SELECT '2dd1cd83-bb9a-4d91-a344-9d3e9b7ed18a', 'Березниковское ЛПУМГ: плановая проверка', 'inspection', 'ready', 'bereznikovskoe', 'Березниковское ЛПУМГ', 'Дулаева Н. И.', '2026-08-21', '2026-08-23', 'Общие, атмосфера, вода, отходы, недра', 'Профиль объекта и пакет ОРД подготовлены', 'Выездная проверка проводится в течение трёх календарных дней.'
            WHERE EXISTS (SELECT 1 FROM app_facilities WHERE slug = 'bereznikovskoe')
              AND NOT EXISTS (SELECT 1 FROM app_schedule_events);
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private bool IsDatabaseConfigured => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("OssDatabase"));
    private static bool IsValid(ScheduleItemRequest request) => !string.IsNullOrWhiteSpace(request.Title)
        && !string.IsNullOrWhiteSpace(request.EventType) && !string.IsNullOrWhiteSpace(request.Status)
        && !string.IsNullOrWhiteSpace(request.FacilitySlug) && !string.IsNullOrWhiteSpace(request.Responsible)
        && request.EndDate >= request.StartDate;
    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
    private static ScheduleItem ReadItem(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateOnly>(7), reader.GetFieldValue<DateOnly>(8), reader.GetString(9), reader.GetString(10), reader.GetString(11));
}

public sealed record ScheduleItem(Guid Id, string Title, string EventType, string Status, string FacilitySlug, string FacilityName, string Responsible, DateOnly StartDate, DateOnly EndDate, string Criteria, string Readiness, string Note);
public sealed record ScheduleItemRequest(string Title, string EventType, string Status, string FacilitySlug, string Responsible, DateOnly StartDate, DateOnly EndDate, string? Criteria, string? Readiness, string? Note);
