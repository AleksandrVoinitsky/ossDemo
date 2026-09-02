using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using RAGify.Abstractions;
using RAGify.Ingestion;

internal sealed class RagService(
    IRagify ragify,
    IVectorStore vectorStore,
    IEmbeddingProvider embeddingProvider,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<RagService> logger)
{
    internal const string Model = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/model_O1.onnx";
    private const string VectorTableName = "ragify_vectors";

    public async Task<RagStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("OssDatabase")))
        {
            return new(false, false, true, 0, 0, Array.Empty<RagDocumentStatus>(), Model,
                "Не задана строка подключения ConnectionStrings__OssDatabase.");
        }

        try
        {
            var documents = await GetIndexedDocumentsFromStoreAsync(cancellationToken);
            var chunkCount = documents.Sum(document => document.ChunkCount);

            return new(chunkCount > 0, true, true, chunkCount, documents.Count, documents, Model, null);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Не удалось получить статус RAGify.");
            return new(false, true, true, 0, 0, Array.Empty<RagDocumentStatus>(), Model, DescribeFailure(exception));
        }
    }

    public async Task<RagSearchResult> SearchAsync(string question, CancellationToken cancellationToken)
    {
        var queryEmbedding = await embeddingProvider.EmbedAsync(question, cancellationToken);
        var results = await vectorStore.SearchAsync(queryEmbedding, topK: 24, threshold: 0.30, cancellationToken: cancellationToken);
        var matches = results
            .Where(result => TryGetMetadataString(result.Metadata, "Text", out var text) && !string.IsNullOrWhiteSpace(text))
            .Select(result => new RagMatch(
                GetMetadataString(result.Metadata, "fileName", "Документ"),
                GetMetadataString(result.Metadata, "heading", "Документ"),
                GetMetadataString(result.Metadata, "Text", string.Empty),
                result.Similarity,
                false,
                false,
                result.Similarity))
            .GroupBy(match => match.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(8)
            .ToArray();
        return new(matches, false, Array.Empty<string>());
    }

    public async Task<RagAnswerResult> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        var searchResult = await SearchAsync(question, cancellationToken);
        if (searchResult.Matches.Count == 0)
        {
            return new("По этому запросу в базе знаний не найдены подходящие фрагменты. Уточните реквизиты документа, период или ключевой термин.", searchResult.Matches);
        }

        using var response = await SendAnswerRequestAsync(question, searchResult.Matches, stream: false, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccessfulQwenResponse(response, payload);

        using var json = JsonDocument.Parse(payload);
        var answer = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException("Qwen вернул пустой ответ.");
        }

        return new(answer, searchResult.Matches);
    }

    public async Task<RagStreamingResult> StartStreamingAnswerAsync(string question, CancellationToken cancellationToken)
    {
        var searchResult = await SearchAsync(question, cancellationToken);
        if (searchResult.Matches.Count == 0)
        {
            return new(null, searchResult.Matches, "По этому запросу в базе знаний не найдены подходящие фрагменты. Уточните реквизиты документа, период или ключевой термин.");
        }

        var response = await SendAnswerRequestAsync(question, searchResult.Matches, stream: true, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccessfulQwenResponse(response, payload);
        }

        return new(response, searchResult.Matches, null);
    }

    private async Task<HttpResponseMessage> SendAnswerRequestAsync(
        string question,
        IReadOnlyList<RagMatch> matches,
        bool stream,
        CancellationToken cancellationToken)
    {
        var context = string.Join("\n\n", matches.Take(3).Select((match, index) =>
            $"[S{index + 1}] {match.DocumentTitle}\n{match.Text}"));
        var token = configuration["AI:ApiToken"]
            ?? throw new InvalidOperationException("Не задан секрет AI__ApiToken.");
        var client = httpClientFactory.CreateClient("AmveraInference");
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = configuration["AI:Model"] ?? "qwen3_30b",
                messages = new[]
                {
                    new InferenceMessage("system", ChatPrompt.BuildSystemMessage(context, true, Array.Empty<string>(), null)),
                    new InferenceMessage("user", question)
                },
                temperature = 0.2,
                max_tokens = 500,
                stream
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        var chunkCount = await vectorStore.GetCountAsync(cancellationToken);
        await ragify.ClearAsync(cancellationToken);
        logger.LogInformation("Очищено векторов RAGify: {ChunkCount}.", chunkCount);
        return chunkCount;
    }

    public async Task<bool> IsSourceImportedAsync(string sourceFileName, string sourceHash, CancellationToken cancellationToken)
    {
        var documentId = CreateDocumentId(sourceFileName).ToString("N");
        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(bool_and(metadata ->> 'sourceHash' = @sourceHash), FALSE)
            FROM {VectorTableName}
            WHERE metadata ->> 'DocumentId' = @documentId
            """, connection);
        command.Parameters.AddWithValue("documentId", documentId);
        command.Parameters.AddWithValue("sourceHash", sourceHash);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<KnowledgeDocumentSummary> IngestAsync(IFormFile file, string? sourceHash, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new RagIngestionException(StatusCodes.Status400BadRequest, "Файл не содержит данных.");
        }

        var sourceFileName = file.FileName.Replace('\\', '/');
        var documentId = CreateDocumentId(sourceFileName);
        var ragifyDocumentId = documentId.ToString("N");
        var metadata = new Dictionary<string, object>
        {
            ["sourceHash"] = sourceHash ?? string.Empty,
            ["fileName"] = sourceFileName,
            ["heading"] = Path.GetFileNameWithoutExtension(sourceFileName)
        };

        try
        {
            await vectorStore.DeleteByDocumentIdAsync(ragifyDocumentId, cancellationToken);
            await using var stream = file.OpenReadStream();
            var document = await DocumentIngestionService.CreateDefault().IngestFromStreamAsync(
                stream,
                sourceFileName,
                ragifyDocumentId,
                file.ContentType,
                metadata,
                cancellationToken);
            await ragify.IngestAsync(document, cancellationToken);
            var chunks = await ragify.GetChunksAsync(ragifyDocumentId, cancellationToken);
            logger.LogInformation("RAGify проиндексировал документ {DocumentId}: {ChunkCount} фрагментов.", ragifyDocumentId, chunks.Count);
            return new(documentId, Path.GetFileNameWithoutExtension(sourceFileName), "ragify", "indexed", sourceFileName,
                DateTimeOffset.UtcNow, file.Length, chunks.Count);
        }
        catch (RagIngestionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "RAGify не смог проиндексировать файл {FileName}.", sourceFileName);
            throw new RagIngestionException(StatusCodes.Status422UnprocessableEntity, "RAGify не смог извлечь или проиндексировать содержимое файла.");
        }
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> GetKnowledgeDocumentsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT metadata ->> 'DocumentId', MIN(metadata ->> 'fileName'), COUNT(*)::integer
            FROM {VectorTableName}
            WHERE metadata ? 'DocumentId' AND metadata ? 'fileName'
            GROUP BY metadata ->> 'DocumentId'
            ORDER BY MIN(metadata ->> 'fileName')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<KnowledgeDocumentSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var documentId = Guid.ParseExact(reader.GetString(0), "N");
            var fileName = reader.GetString(1);
            documents.Add(new(documentId, Path.GetFileNameWithoutExtension(fileName), "ragify", "indexed", fileName,
                DateTimeOffset.MinValue, null, reader.GetInt32(2)));
        }

        return documents;
    }

    public async Task<KnowledgeDocumentContent?> GetKnowledgeDocumentAsync(Guid id, CancellationToken cancellationToken)
    {
        var documents = await GetKnowledgeDocumentsAsync(cancellationToken);
        var document = documents.FirstOrDefault(item => item.Id == id);
        if (document?.OriginalFileName is null)
        {
            return null;
        }

        var path = GetKnowledgeFilePath(document.OriginalFileName);
        if (path is null)
        {
            return null;
        }

        var markdown = await File.ReadAllTextAsync(path, cancellationToken);
        var fileInfo = new FileInfo(path);
        return new(document.Id, document.Title, document.SourceType, document.Status, document.OriginalFileName,
            new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero), fileInfo.Length, markdown, document.ChunkCount);
    }

    internal static string DescribeFailure(Exception exception) => exception switch
    {
        Npgsql.PostgresException postgresException => $"PostgreSQL отклонил операцию ({postgresException.SqlState}): {postgresException.MessageText}",
        Npgsql.NpgsqlException => "Не удалось подключиться к PostgreSQL. Проверьте внутренний хост, имя базы, пользователя и пароль.",
        InvalidOperationException => "RAGify не инициализирован. Проверьте встроенную ONNX-модель и конфигурацию PostgreSQL.",
        _ => "Ошибка RAGify. Проверьте журнал ossDemo."
    };

    private static Guid CreateDocumentId(string sourceFileName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceFileName.ToLowerInvariant()));
        return new Guid(hash[..16]);
    }

    private async Task<List<RagDocumentStatus>> GetIndexedDocumentsFromStoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT metadata ->> 'DocumentId', COUNT(*)::integer
            FROM {VectorTableName}
            WHERE metadata ? 'DocumentId'
            GROUP BY metadata ->> 'DocumentId'
            ORDER BY metadata ->> 'DocumentId'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var documents = new List<RagDocumentStatus>();
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new(reader.GetString(0), "ragify", "indexed", reader.GetInt32(1)));
        }

        return documents;
    }

    private string GetConnectionString() => configuration.GetConnectionString("OssDatabase")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");

    private void EnsureSuccessfulQwenResponse(HttpResponseMessage response, string payload)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        logger.LogError("Qwen вернул HTTP {StatusCode}: {Payload}", (int)response.StatusCode, payload[..Math.Min(payload.Length, 2_000)]);
        throw new HttpRequestException($"Qwen вернул HTTP {(int)response.StatusCode}.");
    }

    private string? GetKnowledgeFilePath(string sourceFileName)
    {
        var directories = new[]
        {
            configuration["KnowledgeImport:Directory"] ?? "/data/inbox",
            Path.Combine(AppContext.BaseDirectory, "knowledge-inbox")
        };
        return directories
            .Select(directory => Path.GetFullPath(Path.Combine(directory, sourceFileName)))
            .FirstOrDefault(File.Exists);
    }

    private static string GetMetadataString(IReadOnlyDictionary<string, object> metadata, string key, string fallback) =>
        TryGetMetadataString(metadata, key, out var value) ? value : fallback;

    private static bool TryGetMetadataString(IReadOnlyDictionary<string, object> metadata, string key, out string value)
    {
        if (!metadata.TryGetValue(key, out var rawValue))
        {
            value = string.Empty;
            return false;
        }

        value = rawValue is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : rawValue?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}

internal sealed record RagMatch(string DocumentTitle, string SourceLabel, string Text, double Similarity, bool HasLexicalMatch, bool IsExactDocumentMatch, double RankingScore)
{
    public bool IsRelevant => Similarity >= 0.30;
}

internal sealed record RagSearchResult(IReadOnlyList<RagMatch> Matches, bool IsAmbiguous, IReadOnlyList<string> AmbiguousDocuments)
{
    public static RagSearchResult Empty { get; } = new(Array.Empty<RagMatch>(), false, Array.Empty<string>());
}

internal sealed record RagAnswerResult(string Answer, IReadOnlyList<RagMatch> Matches);
internal sealed record RagStreamingResult(HttpResponseMessage? UpstreamResponse, IReadOnlyList<RagMatch> Matches, string? ImmediateAnswer);

internal sealed record RagDocumentStatus(string Title, string SourceType, string Status, int ChunkCount);
internal sealed record KnowledgeDocumentSummary(Guid Id, string Title, string SourceType, string Status, string? OriginalFileName, DateTimeOffset UpdatedAt, long? SizeBytes, int ChunkCount);
internal sealed record KnowledgeDocumentContent(Guid Id, string Title, string SourceType, string Status, string? OriginalFileName, DateTimeOffset UpdatedAt, long? SizeBytes, string Markdown, int ChunkCount);
internal sealed class RagIngestionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed record RagStatus(bool Ready, bool DatabaseConfigured, bool EmbeddingsConfigured, int ChunkCount, int DocumentCount, IReadOnlyList<RagDocumentStatus> Documents, string Model, string? Problem);
