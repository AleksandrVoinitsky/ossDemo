using Npgsql;

internal sealed class RagDatabaseInitializer(
    IConfiguration configuration,
    ILogger<RagDatabaseInitializer> logger)
{
    private const string TableName = "ragify_vectors";
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var connectionString = configuration.GetConnectionString("OssDatabase")
                ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");
            await using var connection = new NpgsqlConnection(connectionString);
            logger.LogInformation("RAG: проверяется подключение к PostgreSQL и структура pgvector.");
            await connection.OpenAsync(cancellationToken);

            if (!await IsPgVectorInstalledAsync(connection, cancellationToken))
            {
                throw new InvalidOperationException(
                    "В целевой базе не установлено расширение pgvector (vector). " +
                    "Установите его от имени администратора PostgreSQL, затем повторите индексацию.");
            }

            await ExecuteAsync(connection, $"""
                CREATE TABLE IF NOT EXISTS {TableName} (
                    vector_id TEXT PRIMARY KEY,
                    embedding vector(384),
                    metadata JSONB
                )
                """, cancellationToken);
            await ExecuteAsync(connection, $"""
                CREATE INDEX IF NOT EXISTS {TableName}_embedding_hnsw_idx
                ON {TableName}
                USING hnsw (embedding vector_cosine_ops)
                WITH (m = 16, ef_construction = 64)
                """, cancellationToken);
            await ExecuteAsync(connection, $"""
                CREATE INDEX IF NOT EXISTS {TableName}_text_search_idx
                ON {TableName}
                USING gin (to_tsvector('russian',
                    COALESCE(metadata ->> 'Text', '') || ' ' ||
                    COALESCE(metadata ->> 'fileName', '') || ' ' ||
                    COALESCE(metadata ->> 'heading', '')))
                """, cancellationToken);
            _initialized = true;
            logger.LogInformation("RAG: PostgreSQL, расширение pgvector, таблица {TableName} и индекс готовы.", TableName);
        }
        catch (PostgresException exception)
        {
            logger.LogError(exception, "RAG: PostgreSQL не подготовлен. SQLSTATE={SqlState}, сообщение={MessageText}.", exception.SqlState, exception.MessageText);
            throw;
        }
        catch (NpgsqlException exception)
        {
            logger.LogError(exception, "RAG: не удалось подключиться к PostgreSQL.");
            throw;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static async Task<bool> IsPgVectorInstalledAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')",
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Не удалось проверить наличие расширения pgvector."));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
