using Npgsql;

public sealed class OperationalDataService(IConfiguration configuration, ILogger<OperationalDataService> logger)
{
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private bool initialized;

    public async Task<OperationalDashboard> GetDashboardAsync(CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured)
        {
            return OperationalDashboard.Unavailable;
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        return new OperationalDashboard(
            await CountAsync(connection, "app_facilities", cancellationToken),
            await CountAsync(connection, "app_violations", cancellationToken),
            await CountAsync(connection, "app_violations WHERE status IN ('critical', 'review')", cancellationToken),
            await GetUpcomingInspectionsAsync(connection, cancellationToken),
            await GetRecentActivityAsync(connection, cancellationToken));
    }

    public async Task<IReadOnlyList<OperationalFacility>> GetFacilitiesAsync(CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured)
        {
            return Array.Empty<OperationalFacility>();
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, name, address, nvoc_category, latitude, longitude FROM app_facilities ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<OperationalFacility>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new OperationalFacility(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5)));
        return items;
    }

    public async Task<IReadOnlyList<OperationalViolation>> GetViolationsAsync(CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured)
        {
            return Array.Empty<OperationalViolation>();
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, created_at, facility_name, classifier_section, description, responsible, due_date, status FROM app_violations ORDER BY created_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<OperationalViolation>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new OperationalViolation(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6), reader.GetString(7)));
        return items;
    }

    public async Task<OperationalCommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured)
        {
            return OperationalCommandResult.DatabaseUnavailable;
        }

        await EnsureInitializedAsync(cancellationToken);
        var parts = command.Trim().Split('|', StringSplitOptions.TrimEntries);
        var action = parts[0].Trim().ToLowerInvariant();

        if (action is "!help" or "!commands")
        {
            return OperationalCommandResult.Help;
        }

        if (action == "!add-facility")
        {
            if (parts.Length != 6)
            {
                return OperationalCommandResult.Invalid("Формат: `!add-facility | Наименование | Адрес | Категория НВОС | Широта | Долгота`");
            }

            if (!decimal.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture, out var latitude)
                || !decimal.TryParse(parts[5], System.Globalization.CultureInfo.InvariantCulture, out var longitude))
            {
                return OperationalCommandResult.Invalid("Широту и долготу укажите числами в формате `59.4072`.");
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO app_facilities (name, address, nvoc_category, latitude, longitude)
                VALUES (@name, @address, @category, @latitude, @longitude)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("name", parts[1]);
                insert.Parameters.AddWithValue("address", parts[2]);
                insert.Parameters.AddWithValue("category", parts[3]);
                insert.Parameters.AddWithValue("latitude", latitude);
                insert.Parameters.AddWithValue("longitude", longitude);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await WriteAuditAsync(connection, transaction, "Добавлен объект", "Объекты", parts[1], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationalCommandResult.Success($"Объект **{parts[1]}** создан и добавлен в реестр.");
        }

        if (action == "!add-violation")
        {
            if (parts.Length != 7)
            {
                return OperationalCommandResult.Invalid("Формат: `!add-violation | Объект | Раздел | Формулировка | Ответственный | ГГГГ-ММ-ДД | critical|review|closed`");
            }

            if (!DateOnly.TryParse(parts[5], out var dueDate) || !new[] { "critical", "review", "closed" }.Contains(parts[6], StringComparer.OrdinalIgnoreCase))
            {
                return OperationalCommandResult.Invalid("Укажите срок в формате `ГГГГ-ММ-ДД` и статус: `critical`, `review` или `closed`.");
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO app_violations (facility_name, classifier_section, description, responsible, due_date, status)
                VALUES (@facility, @section, @description, @responsible, @dueDate, @status)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("facility", parts[1]);
                insert.Parameters.AddWithValue("section", parts[2]);
                insert.Parameters.AddWithValue("description", parts[3]);
                insert.Parameters.AddWithValue("responsible", parts[4]);
                insert.Parameters.AddWithValue("dueDate", dueDate);
                insert.Parameters.AddWithValue("status", parts[6].ToLowerInvariant());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await WriteAuditAsync(connection, transaction, "Зарегистрировано нарушение", "Нарушения", parts[3], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationalCommandResult.Success("Нарушение зарегистрировано и появилось в реестре.");
        }

        return OperationalCommandResult.NotHandled;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS app_facilities (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL UNIQUE, address TEXT NOT NULL,
                    nvoc_category TEXT NOT NULL, latitude NUMERIC(9,6) NOT NULL, longitude NUMERIC(9,6) NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT now());
                CREATE TABLE IF NOT EXISTS app_violations (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(), facility_name TEXT NOT NULL, classifier_section TEXT NOT NULL,
                    description TEXT NOT NULL, responsible TEXT NOT NULL, due_date DATE, status TEXT NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT now());
                CREATE TABLE IF NOT EXISTS app_audit_log (
                    id BIGSERIAL PRIMARY KEY, occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), actor TEXT NOT NULL DEFAULT 'inspector',
                    action TEXT NOT NULL, entity_type TEXT NOT NULL, details TEXT NOT NULL);
                INSERT INTO app_facilities (name, address, nvoc_category, latitude, longitude)
                VALUES ('Березниковское ЛПУМГ', 'Пермский край, район г. Березники', 'I', 59.4072, 56.8040)
                ON CONFLICT (name) DO NOTHING;
                INSERT INTO app_violations (facility_name, classifier_section, description, responsible, due_date, status)
                SELECT 'Березниковское ЛПУМГ', '2.3 Атмосфера', 'Не представлен протокол инструментального контроля', 'Главный инженер', '2026-10-10', 'critical'
                WHERE NOT EXISTS (SELECT 1 FROM app_violations);
                INSERT INTO app_audit_log (action, entity_type, details)
                SELECT 'Инициализирован рабочий реестр', 'Система', 'Созданы прикладные таблицы для объектов, нарушений и аудита.'
                WHERE NOT EXISTS (SELECT 1 FROM app_audit_log);
                """, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            initialized = true;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger.LogError(exception, "Не удалось подготовить прикладные таблицы ООС.");
            throw;
        }
        finally { initializationLock.Release(); }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("OssDatabase")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private bool IsDatabaseConfigured => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("OssDatabase"));

    private static async Task<int> CountAsync(NpgsqlConnection connection, string tableExpression, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {tableExpression}", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<OperationalInspection>> GetUpcomingInspectionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT name, nvoc_category FROM app_facilities ORDER BY created_at DESC LIMIT 4", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<OperationalInspection>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new OperationalInspection(reader.GetString(0), reader.GetString(1)));
        return items;
    }

    private static async Task<IReadOnlyList<OperationalActivity>> GetRecentActivityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT occurred_at, action, entity_type, details FROM app_audit_log ORDER BY occurred_at DESC LIMIT 6", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<OperationalActivity>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new OperationalActivity(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return items;
    }

    private static async Task WriteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action, string entityType, string details, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO app_audit_log (action, entity_type, details) VALUES (@action, @entityType, @details)", connection, transaction);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityType", entityType);
        command.Parameters.AddWithValue("details", details);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record OperationalDashboard(int FacilityCount, int ViolationCount, int AttentionCount, IReadOnlyList<OperationalInspection> Inspections, IReadOnlyList<OperationalActivity> Activity)
{
    public static OperationalDashboard Unavailable { get; } = new(0, 0, 0, Array.Empty<OperationalInspection>(), Array.Empty<OperationalActivity>());
}
public sealed record OperationalInspection(string Name, string NvocCategory);
public sealed record OperationalActivity(DateTimeOffset OccurredAt, string Action, string EntityType, string Details);
public sealed record OperationalFacility(Guid Id, string Name, string Address, string NvocCategory, decimal Latitude, decimal Longitude);
public sealed record OperationalViolation(Guid Id, DateTimeOffset CreatedAt, string FacilityName, string ClassifierSection, string Description, string Responsible, DateOnly? DueDate, string Status);
public sealed record OperationalCommandResult(bool IsHandled, bool IsSuccess, string Answer)
{
    public static OperationalCommandResult NotHandled { get; } = new(false, false, string.Empty);
    public static OperationalCommandResult DatabaseUnavailable { get; } = new(true, false, "**Рабочий реестр недоступен.** Для создания записей задайте `ConnectionStrings__OssDatabase` и перезапустите приложение.");
    public static OperationalCommandResult Help { get; } = new(true, true, """
        ## Команды рабочего реестра

        Команды сохраняют записи в PostgreSQL и фиксируются в журнале действий.

        - `!add-facility | Наименование | Адрес | Категория НВОС | Широта | Долгота`
        - `!add-violation | Объект | Раздел | Формулировка | Ответственный | ГГГГ-ММ-ДД | critical|review|closed`

        Для диагностики RAG по-прежнему доступны `!status`, `!statusrag`, `!reindex` и `!текст запроса`.
        """);
    public static OperationalCommandResult Invalid(string answer) => new(true, false, $"**Запись не создана.** {answer}");
    public static OperationalCommandResult Success(string answer) => new(true, true, answer);
}
