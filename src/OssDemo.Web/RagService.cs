using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

internal sealed class RagService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<RagService> logger)
{
    private const string Model = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2";
    private static readonly Regex GostNumberPattern = new(@"\bГОСТ\s*(?:Р\s*)?(\d{4,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GovernmentResolutionNumberPattern = new(@"\b(?:постановлени[ея]|пп)\s+(?:правительств[ао]\s*)?(?:рф\s*)?(?:№|N)?\s*(\d{1,5})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
                    markdown text NULL,
                    original_file_name text NULL,
                    content_type text NULL,
                    size_bytes bigint NULL,
                    source_hash text NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                ALTER TABLE knowledge_documents ADD COLUMN IF NOT EXISTS markdown text NULL;
                ALTER TABLE knowledge_documents ADD COLUMN IF NOT EXISTS original_file_name text NULL;
                ALTER TABLE knowledge_documents ADD COLUMN IF NOT EXISTS content_type text NULL;
                ALTER TABLE knowledge_documents ADD COLUMN IF NOT EXISTS size_bytes bigint NULL;
                ALTER TABLE knowledge_documents ADD COLUMN IF NOT EXISTS source_hash text NULL;
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
                UPDATE knowledge_documents d
                SET markdown = concat('# ', d.title, E'\n\n', c.text),
                    original_file_name = coalesce(d.original_file_name, concat(d.title, '.md'))
                FROM knowledge_chunks c
                WHERE c.document_id = d.id AND c.ordinal = 1 AND d.markdown IS NULL;
                """, cancellationToken);

            _initialized = true;
            logger.LogInformation("RAG-схема готова.");
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
        catch (Exception exception)
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
        var referencedGostNumber = GostNumberPattern.Match(question).Groups[1].Value;
        var referencedResolutionNumber = GovernmentResolutionNumberPattern.Match(question).Groups[1].Value;

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.title,
                   c.source_label,
                   c.text,
                   1 - (c.embedding <=> CAST(@embedding AS vector)) AS similarity,
                   position(lower(@question) in lower(d.title)) > 0 AS title_match,
                   @gostNumber <> '' AND position(lower(@gostNumber) in lower(d.title)) > 0 AS gost_match,
                   @resolutionNumber <> ''
                       AND d.title ~* ('постановлени[ея].*правительств')
                       AND d.title ~ ('(^|[^0-9])' || @resolutionNumber || '([^0-9]|$)') AS resolution_match
            FROM knowledge_chunks c
            INNER JOIN knowledge_documents d ON d.id = c.document_id
            WHERE d.status = 'indexed' AND c.embedding_model = @model
            ORDER BY @resolutionNumber <> ''
                         AND d.title ~* ('постановлени[ея].*правительств')
                         AND d.title ~ ('(^|[^0-9])' || @resolutionNumber || '([^0-9]|$)') DESC,
                     @gostNumber <> '' AND position(lower(@gostNumber) in lower(d.title)) > 0 DESC,
                     position(lower(@question) in lower(d.title)) > 0 DESC,
                     c.embedding <=> CAST(@embedding AS vector)
            LIMIT 5;
            """, connection);
        command.Parameters.AddWithValue("embedding", vector);
        command.Parameters.AddWithValue("question", question);
        command.Parameters.AddWithValue("gostNumber", referencedGostNumber);
        command.Parameters.AddWithValue("resolutionNumber", referencedResolutionNumber);
        command.Parameters.AddWithValue("model", Model);

        var matches = new List<RagMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var similarity = reader.GetDouble(3);
            var titleMatch = reader.GetBoolean(4);
            var gostMatch = reader.GetBoolean(5);
            var resolutionMatch = reader.GetBoolean(6);
            if (resolutionMatch || gostMatch || titleMatch || similarity >= 0.35)
            {
                matches.Add(new RagMatch(reader.GetString(0), reader.GetString(1), reader.GetString(2), resolutionMatch || gostMatch || titleMatch ? 1 : similarity));
            }
        }

        return matches;
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> GetKnowledgeDocumentsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_initialized)
        {
            return Array.Empty<KnowledgeDocumentSummary>();
        }

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.id, d.title, d.source_type, d.status, d.original_file_name, d.updated_at, d.size_bytes, count(c.id)
            FROM knowledge_documents d
            LEFT JOIN knowledge_chunks c ON c.document_id = d.id
            WHERE d.markdown IS NOT NULL
            GROUP BY d.id, d.title, d.source_type, d.status, d.original_file_name, d.updated_at, d.size_bytes
            ORDER BY d.updated_at DESC, d.title;
            """, connection);

        var documents = new List<KnowledgeDocumentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new KnowledgeDocumentSummary(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt32(7)));
        }

        return documents;
    }

    public async Task<KnowledgeDocumentContent?> GetKnowledgeDocumentAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_initialized)
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.id, d.title, d.source_type, d.status, d.original_file_name, d.updated_at, d.size_bytes, d.markdown, count(c.id)
            FROM knowledge_documents d
            LEFT JOIN knowledge_chunks c ON c.document_id = d.id
            WHERE d.id = @id AND d.markdown IS NOT NULL
            GROUP BY d.id, d.title, d.source_type, d.status, d.original_file_name, d.updated_at, d.size_bytes, d.markdown;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KnowledgeDocumentContent(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7),
            reader.GetInt32(8));
    }

    public async Task<bool> IsSourceImportedAsync(string fileName, string sourceHash, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_initialized)
        {
            return false;
        }

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM knowledge_documents
                WHERE original_file_name = @fileName AND source_hash = @sourceHash AND status = 'indexed'
            );
            """, connection);
        command.Parameters.AddWithValue("fileName", fileName);
        command.Parameters.AddWithValue("sourceHash", sourceHash);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<KnowledgeDocumentSummary> IngestAsync(IFormFile file, string? sourceHash, CancellationToken cancellationToken)
    {
        const long maxFileSize = 20 * 1024 * 1024;
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (file.Length == 0)
        {
            throw new RagIngestionException(StatusCodes.Status400BadRequest, "Выберите непустой файл.");
        }

        if (file.Length > maxFileSize)
        {
            throw new RagIngestionException(StatusCodes.Status400BadRequest, "Размер файла не должен превышать 20 МБ.");
        }

        if (extension is not ".pdf" and not ".docx" and not ".xlsx" and not ".txt" and not ".md")
        {
            throw new RagIngestionException(StatusCodes.Status400BadRequest, "Поддерживаются файлы PDF, DOCX, XLSX, TXT и Markdown.");
        }

        var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new RagIngestionException(StatusCodes.Status400BadRequest, "У файла должно быть имя.");
        }

        var markdown = extension is ".txt" or ".md"
            ? await ReadPlainTextAsync(file, cancellationToken)
            : await ConvertToMarkdownAsync(file, cancellationToken);
        var chunks = SplitMarkdown(markdown);
        if (chunks.Count == 0)
        {
            throw new RagIngestionException(StatusCodes.Status422UnprocessableEntity, "Файл не содержит текста для индексации.");
        }

        await InitializeAsync(cancellationToken);
        if (!_initialized)
        {
            throw new RagIngestionException(StatusCodes.Status503ServiceUnavailable, "База знаний не настроена.");
        }

        var embeddings = new List<float[]>(chunks.Count);
        foreach (var chunk in chunks)
        {
            embeddings.Add(await CreateEmbeddingAsync(chunk.Text, cancellationToken));
        }

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid documentId;
        await using (var findDocument = new NpgsqlCommand("SELECT id FROM knowledge_documents WHERE title = @title;", connection, transaction))
        {
            findDocument.Parameters.AddWithValue("title", title);
            var existing = await findDocument.ExecuteScalarAsync(cancellationToken);
            documentId = existing is Guid existingId ? existingId : Guid.NewGuid();
        }

        await using (var upsertDocument = new NpgsqlCommand("""
            INSERT INTO knowledge_documents (id, title, source_type, status, markdown, original_file_name, content_type, size_bytes, source_hash)
            VALUES (@id, @title, 'volume', 'indexed', @markdown, @fileName, @contentType, @sizeBytes, @sourceHash)
            ON CONFLICT (title) DO UPDATE SET
                source_type = 'volume',
                status = EXCLUDED.status,
                markdown = EXCLUDED.markdown,
                original_file_name = EXCLUDED.original_file_name,
                content_type = EXCLUDED.content_type,
                size_bytes = EXCLUDED.size_bytes,
                source_hash = EXCLUDED.source_hash,
                updated_at = now();
            """, connection, transaction))
        {
            upsertDocument.Parameters.AddWithValue("id", documentId);
            upsertDocument.Parameters.AddWithValue("title", title);
            upsertDocument.Parameters.AddWithValue("markdown", markdown);
            upsertDocument.Parameters.AddWithValue("fileName", file.FileName);
            upsertDocument.Parameters.AddWithValue("contentType", string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Application.Octet : file.ContentType);
            upsertDocument.Parameters.AddWithValue("sizeBytes", file.Length);
            upsertDocument.Parameters.AddWithValue("sourceHash", (object?)sourceHash ?? DBNull.Value);
            await upsertDocument.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteChunks = new NpgsqlCommand("DELETE FROM knowledge_chunks WHERE document_id = @documentId;", connection, transaction))
        {
            deleteChunks.Parameters.AddWithValue("documentId", documentId);
            await deleteChunks.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < chunks.Count; index++)
        {
            await using var insertChunk = new NpgsqlCommand("""
                INSERT INTO knowledge_chunks (id, document_id, ordinal, source_label, text, embedding, embedding_model)
                VALUES (@id, @documentId, @ordinal, @sourceLabel, @text, CAST(@embedding AS vector), @model);
                """, connection, transaction);
            insertChunk.Parameters.AddWithValue("id", Guid.NewGuid());
            insertChunk.Parameters.AddWithValue("documentId", documentId);
            insertChunk.Parameters.AddWithValue("ordinal", index + 1);
            insertChunk.Parameters.AddWithValue("sourceLabel", chunks[index].SourceLabel);
            insertChunk.Parameters.AddWithValue("text", chunks[index].Text);
            insertChunk.Parameters.AddWithValue("embedding", ToVectorLiteral(embeddings[index]));
            insertChunk.Parameters.AddWithValue("model", Model);
            await insertChunk.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Импортирован и проиндексирован документ {Title}: {ChunkCount} фрагментов.", title, chunks.Count);
        return new KnowledgeDocumentSummary(documentId, title, "volume", "indexed", file.FileName, DateTimeOffset.UtcNow, file.Length, chunks.Count);
    }

    private async Task<string> ConvertToMarkdownAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["Docling:BaseUrl"];
        var apiKey = configuration["Docling:ApiKey"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RagIngestionException(StatusCodes.Status503ServiceUnavailable, "Сервис преобразования документов не настроен.");
        }

        using var form = new MultipartFormDataContent();
        await using var fileStream = file.OpenReadStream();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType)
            ? MediaTypeNames.Application.Octet
            : file.ContentType);
        form.Add(fileContent, "files", Path.GetFileName(file.FileName));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(serviceUri, "v1/convert/file"))
        {
            Content = form
        };
        request.Headers.Add("X-Api-Key", apiKey);

        using var response = await httpClientFactory.CreateClient("Docling").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Docling вернул статус {StatusCode} для файла {FileName}.", (int)response.StatusCode, Path.GetFileName(file.FileName));
            throw new RagIngestionException(StatusCodes.Status502BadGateway, "Docling не смог преобразовать файл. Проверьте формат и повторите попытку.");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = payload.RootElement;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        var document = root.TryGetProperty("document", out var documentElement)
            ? documentElement
            : root.TryGetProperty("content", out var legacyDocumentElement) ? legacyDocumentElement : default;
        var markdown = document.ValueKind == JsonValueKind.Object && document.TryGetProperty("md_content", out var markdownElement)
            ? markdownElement.GetString()
            : null;

        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(markdown))
        {
            logger.LogWarning("Docling не вернул Markdown для файла {FileName}; статус {Status}.", Path.GetFileName(file.FileName), status);
            throw new RagIngestionException(StatusCodes.Status422UnprocessableEntity, "Docling не вернул Markdown для этого файла.");
        }

        return markdown.Trim();
    }

    private static async Task<string> ReadPlainTextAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(cancellationToken)).Trim();
    }

    private static List<MarkdownChunk> SplitMarkdown(string markdown)
    {
        const int maxChunkLength = 1_800;
        var chunks = new List<MarkdownChunk>();
        var buffer = new List<string>();
        var length = 0;
        var sourceLabel = "Документ";

        void Flush()
        {
            var text = string.Join("\n", buffer).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                chunks.Add(new MarkdownChunk(sourceLabel, text));
            }

            buffer.Clear();
            length = 0;
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(heading))
                {
                    if (length > maxChunkLength / 2)
                    {
                        Flush();
                    }

                    sourceLabel = heading;
                }
            }

            if (length > 0 && length + line.Length + 1 > maxChunkLength)
            {
                Flush();
            }

            if (line.Length > maxChunkLength)
            {
                for (var start = 0; start < line.Length; start += maxChunkLength)
                {
                    var partLength = Math.Min(maxChunkLength, line.Length - start);
                    buffer.Add(line.Substring(start, partLength));
                    Flush();
                }
                continue;
            }

            buffer.Add(line);
            length += line.Length + 1;
        }

        Flush();
        return chunks;
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
        ArgumentException => "Строка подключения PostgreSQL имеет неверный формат. Если пароль содержит ;, кавычку или пробел, заключите значение Password в двойные кавычки и экранируйте двойную кавычку повтором.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Embedding-сервис ответил 401: ключ Embeddings__ApiKey не совпадает с EMBEDDINGS_API_KEY в minilm.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } => "Embedding-сервис ответил 503: minilm ещё загружает модель либо не запущен.",
        HttpRequestException { StatusCode: not null } httpException => $"Embedding-сервис ответил HTTP {(int)httpException.StatusCode.Value}. Проверьте журнал minilm.",
        HttpRequestException => "Не удалось установить соединение с embedding-сервисом. Проверьте, что minilm запущен и использует внутренний адрес Amvera.",
        JsonException => "Embedding-сервис вернул ответ в неожиданном формате.",
        InvalidOperationException => "Embedding-сервис не настроен либо вернул вектор с неверной моделью или размерностью.",
        _ => "Ошибка инициализации RAG неизвестного типа. Проверьте журнал ossDemo."
    };

    private sealed record MarkdownChunk(string SourceLabel, string Text);
}

internal sealed record RagMatch(string DocumentTitle, string SourceLabel, string Text, double Similarity);
internal sealed record RagDocumentStatus(string Title, string SourceType, string Status, int ChunkCount);
internal sealed record KnowledgeDocumentSummary(
    Guid Id,
    string Title,
    string SourceType,
    string Status,
    string? OriginalFileName,
    DateTimeOffset UpdatedAt,
    long? SizeBytes,
    int ChunkCount);
internal sealed record KnowledgeDocumentContent(
    Guid Id,
    string Title,
    string SourceType,
    string Status,
    string? OriginalFileName,
    DateTimeOffset UpdatedAt,
    long? SizeBytes,
    string Markdown,
    int ChunkCount);
internal sealed class RagIngestionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
internal sealed record RagStatus(
    bool Ready,
    bool DatabaseConfigured,
    bool EmbeddingsConfigured,
    int ChunkCount,
    int DocumentCount,
    IReadOnlyList<RagDocumentStatus> Documents,
    string Model,
    string? Problem);
