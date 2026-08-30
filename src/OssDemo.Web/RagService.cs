using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

internal sealed class RagService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<RagService> logger)
{
    private const string Model = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2";
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var connectionString = configuration.GetConnectionString("OssDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("RAG не инициализирован: отсутствует ConnectionStrings__OssDatabase.");
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS knowledge_documents (
                    id uuid PRIMARY KEY,
                    title text NOT NULL UNIQUE,
                    source_type text NOT NULL,
                    status text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                CREATE TABLE IF NOT EXISTS knowledge_chunks (
                    id uuid PRIMARY KEY,
                    document_id uuid NOT NULL REFERENCES knowledge_documents(id) ON DELETE CASCADE,
                    ordinal integer NOT NULL,
                    source_label text NOT NULL,
                    text text NOT NULL,
                    embedding vector(384) NOT NULL,
                    embedding_model text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    UNIQUE(document_id, ordinal)
                );
                CREATE INDEX IF NOT EXISTS ix_knowledge_chunks_embedding_hnsw
                    ON knowledge_chunks USING hnsw (embedding vector_cosine_ops)
                    WITH (m = 16, ef_construction = 64);
                CREATE INDEX IF NOT EXISTS ix_knowledge_chunks_document_ordinal
                    ON knowledge_chunks(document_id, ordinal);
                """, cancellationToken);

            await SeedDemoDocumentsAsync(connection, cancellationToken);
            _initialized = true;
            logger.LogInformation("RAG-схема готова, demo-источники проиндексированы.");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<RagStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var hasDatabase = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("OssDatabase"));
        var hasEmbeddings = Uri.TryCreate(configuration["Embeddings:BaseUrl"], UriKind.Absolute, out _)
            && !string.IsNullOrWhiteSpace(configuration["Embeddings:ApiKey"]);

        if (!hasDatabase || !hasEmbeddings)
        {
            var problem = !hasDatabase
                ? "Не задана строка подключения ConnectionStrings__OssDatabase."
                : "Не задана конфигурация embedding-сервиса: Embeddings__BaseUrl или Embeddings__ApiKey.";
            return new(false, hasDatabase, hasEmbeddings, 0, 0, Array.Empty<RagDocumentStatus>(), Model, problem);
        }

        try
        {
            await InitializeAsync(cancellationToken);
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT count(*) FROM knowledge_chunks;", connection);
            var chunkCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            var documents = new List<RagDocumentStatus>();
            await using var documentsCommand = new NpgsqlCommand("""
                SELECT d.title, d.source_type, d.status, count(c.id)
                FROM knowledge_documents d
                LEFT JOIN knowledge_chunks c ON c.document_id = d.id
                GROUP BY d.id, d.title, d.source_type, d.status
                ORDER BY d.title;
                """, connection);
            await using var reader = await documentsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(new RagDocumentStatus(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }

            return new(_initialized && chunkCount > 0, true, true, chunkCount, documents.Count, documents, Model, null);
        }
        catch (Exception exception) when (exception is NpgsqlException or HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogError(exception, "Не удалось получить статус RAG.");
            return new(false, hasDatabase, hasEmbeddings, 0, 0, Array.Empty<RagDocumentStatus>(), Model, DescribeStatusFailure(exception));
        }
    }

    public async Task<IReadOnlyList<RagMatch>> SearchAsync(string question, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_initialized)
        {
            return Array.Empty<RagMatch>();
        }

        var embedding = await CreateEmbeddingAsync(question, cancellationToken);
        var vector = ToVectorLiteral(embedding);

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.title, c.source_label, c.text, 1 - (c.embedding <=> CAST(@embedding AS vector)) AS similarity
            FROM knowledge_chunks c
            INNER JOIN knowledge_documents d ON d.id = c.document_id
            WHERE d.status = 'indexed' AND c.embedding_model = @model
            ORDER BY c.embedding <=> CAST(@embedding AS vector)
            LIMIT 5;
            """, connection);
        command.Parameters.AddWithValue("embedding", vector);
        command.Parameters.AddWithValue("model", Model);

        var matches = new List<RagMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var similarity = reader.GetDouble(3);
            if (similarity >= 0.35)
            {
                matches.Add(new RagMatch(reader.GetString(0), reader.GetString(1), reader.GetString(2), similarity));
            }
        }

        return matches;
    }

    private async Task SeedDemoDocumentsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var documents = new[]
        {
            new DemoDocument("7-ФЗ «Об охране окружающей среды»", "federal_law", "статья 67", "Производственный экологический контроль осуществляется в целях обеспечения соблюдения требований в области охраны окружающей среды. Природопользователь обязан соблюдать утверждённые нормативы и вести учёт воздействия на окружающую среду."),
            new DemoDocument("89-ФЗ «Об отходах производства и потребления»", "federal_law", "статья 11", "Организация должна обеспечивать учёт отходов и соблюдать установленные требования при обращении с отходами, включая накопление, размещение и документирование операций."),
            new DemoDocument("СТО 16-005-2025 «Обращение с отходами»", "corporate_standard", "раздел 5.3.2", "При инспекционном контроле обращения с отходами проверяется наличие мест накопления, маркировка контейнеров, документы учёта и соблюдение нормативов образования отходов и лимитов размещения."),
            new DemoDocument("Программа ПЭК Березниковского ЛПУМГ", "ord", "раздел «Контрольные действия»", "Программа производственного экологического контроля устанавливает порядок контроля на объекте. Результаты измерений, журналы и сведения об ответственных лицах должны быть доступны для проверки."),
            new DemoDocument("Акт проверки от 22.09.2024", "violation_material", "нарушение по протоколам", "Нарушение по протоколам инструментального контроля выбросов не устранено в установленный срок до 10.10.2024. Пункт подлежит включению в чек-лист как критический до подтверждения устранения нарушения.")
        };

        foreach (var document in documents)
        {
            await using var existsCommand = new NpgsqlCommand("SELECT id FROM knowledge_documents WHERE title = @title;", connection);
            existsCommand.Parameters.AddWithValue("title", document.Title);
            var existingId = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (existingId is not null)
            {
                continue;
            }

            var documentId = Guid.NewGuid();
            var embedding = await CreateEmbeddingAsync(document.Text, cancellationToken);
            await using var insertDocument = new NpgsqlCommand("""
                INSERT INTO knowledge_documents (id, title, source_type, status)
                VALUES (@id, @title, @sourceType, 'indexed');
                """, connection);
            insertDocument.Parameters.AddWithValue("id", documentId);
            insertDocument.Parameters.AddWithValue("title", document.Title);
            insertDocument.Parameters.AddWithValue("sourceType", document.SourceType);
            await insertDocument.ExecuteNonQueryAsync(cancellationToken);

            await using var insertChunk = new NpgsqlCommand("""
                INSERT INTO knowledge_chunks (id, document_id, ordinal, source_label, text, embedding, embedding_model)
                VALUES (@id, @documentId, 1, @sourceLabel, @text, CAST(@embedding AS vector), @model);
                """, connection);
            insertChunk.Parameters.AddWithValue("id", Guid.NewGuid());
            insertChunk.Parameters.AddWithValue("documentId", documentId);
            insertChunk.Parameters.AddWithValue("sourceLabel", document.SourceLabel);
            insertChunk.Parameters.AddWithValue("text", document.Text);
            insertChunk.Parameters.AddWithValue("embedding", ToVectorLiteral(embedding));
            insertChunk.Parameters.AddWithValue("model", Model);
            await insertChunk.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["Embeddings:BaseUrl"];
        var apiKey = configuration["Embeddings:ApiKey"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Embedding-сервис не настроен.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(serviceUri, "embed"))
        {
            Content = JsonContent.Create(new { inputs = new[] { text } })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClientFactory.CreateClient("Embeddings").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = payload.RootElement;
        var responseModel = root.GetProperty("model").GetString();
        var values = root.GetProperty("embeddings")[0].EnumerateArray().Select(value => value.GetSingle()).ToArray();

        if (!string.Equals(responseModel, Model, StringComparison.Ordinal) || values.Length != 384 || values.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidOperationException("Embedding-сервис вернул модель или вектор в неожиданном формате.");
        }

        return values;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToVectorLiteral(IEnumerable<float> values) =>
        $"[{string.Join(',', values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))}]";

    private static string DescribeStatusFailure(Exception exception) => exception switch
    {
        PostgresException { SqlState: "42501" } => "У пользователя PostgreSQL недостаточно прав для создания расширения vector или таблиц базы знаний.",
        PostgresException { SqlState: "0A000" } => "Расширение pgvector недоступно в этом экземпляре PostgreSQL.",
        PostgresException { SqlState: "3D000" } => "База данных из строки подключения не найдена.",
        PostgresException => "PostgreSQL отклонил запрос инициализации. Проверьте журнал ossDemo.",
        NpgsqlException => "Не удалось подключиться к PostgreSQL. Проверьте внутренний хост, имя базы, пользователя и пароль.",
        HttpRequestException => "Embedding-сервис недоступен или ключ Embeddings__ApiKey не совпадает с EMBEDDINGS_API_KEY в minilm.",
        JsonException => "Embedding-сервис вернул ответ в неожиданном формате.",
        InvalidOperationException => "Embedding-сервис не настроен либо вернул вектор с неверной моделью или размерностью.",
        _ => "Не удалось инициализировать RAG. Проверьте журнал ossDemo."
    };

    private sealed record DemoDocument(string Title, string SourceType, string SourceLabel, string Text);
}

internal sealed record RagMatch(string DocumentTitle, string SourceLabel, string Text, double Similarity);
internal sealed record RagDocumentStatus(string Title, string SourceType, string Status, int ChunkCount);
internal sealed record RagStatus(
    bool Ready,
    bool DatabaseConfigured,
    bool EmbeddingsConfigured,
    int ChunkCount,
    int DocumentCount,
    IReadOnlyList<RagDocumentStatus> Documents,
    string Model,
    string? Problem);
