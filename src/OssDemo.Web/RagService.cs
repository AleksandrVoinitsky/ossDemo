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
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<RagService> logger,
    MultilingualCrossEncoderReranker? reranker = null)
{
    internal const string Model = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/model_O1.onnx";
    private const string VectorTableName = "ragify_vectors";
    private const int CandidateCount = 24;
    private const int ResultCount = 8;

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
        => await SearchAsync(question, question, cancellationToken);

    private async Task<RagSearchResult> SearchAsync(string question, string searchQuery, CancellationToken cancellationToken)
    {
        var candidates = await RetrieveCandidatesAsync(searchQuery, cancellationToken);
        var matches = candidates.Take(ResultCount).ToArray();
        if (reranker is not null)
        {
            try
            {
                matches = reranker.Rerank(question, candidates, ResultCount).ToArray();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cross-encoder reranker не выполнил оценку кандидатов. Использовано ранжирование RAGify.");
            }
        }
        return new(matches, false, Array.Empty<string>());
    }

    public async Task<RagSearchResult> SearchBeforeRerankAsync(string query, CancellationToken cancellationToken)
    {
        var candidates = await RetrieveCandidatesAsync(query, cancellationToken);
        return new(candidates, false, Array.Empty<string>());
    }

    private async Task<IReadOnlyList<RagMatch>> RetrieveCandidatesAsync(string searchQuery, CancellationToken cancellationToken)
    {
        var result = await ragify.QueryAsync(searchQuery, new QueryOptions
        {
            Retrieval = new RetrievalOptions
            {
                TopK = CandidateCount,
                SimilarityThreshold = 0,
                EnableDynamicTopK = false,
                EnableDeduplication = true
            }
        }, cancellationToken);

        return result.Context
            .Where(context => !string.IsNullOrWhiteSpace(context.Chunk.Text))
            .Select(context => new RagMatch(
                GetMetadataString(context.Chunk.Metadata, "fileName", context.Source ?? "Документ"),
                GetMetadataString(context.Chunk.Metadata, "heading", "Документ"),
                context.Chunk.Text,
                context.Similarity,
                context.Similarity))
            .ToArray();
    }

    public async Task<RagAnswerResult> AnswerAsync(
        string question,
        IReadOnlyList<ChatHistoryMessage> conversation,
        CancellationToken cancellationToken)
    {
        var searchQuery = ChatSearchQuery.Build(conversation, question, maxLength: 6_000);
        var searchResult = await SearchAsync(question, searchQuery, cancellationToken);
        using var response = await SendAnswerRequestAsync(question, searchResult.Matches, conversation, stream: false, cancellationToken);
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

    public async Task<RagStreamingResult> StartStreamingAnswerAsync(
        string question,
        IReadOnlyList<ChatHistoryMessage> conversation,
        CancellationToken cancellationToken)
    {
        var searchQuery = ChatSearchQuery.Build(conversation, question, maxLength: 6_000);
        var searchResult = await SearchAsync(question, searchQuery, cancellationToken);
        var response = await SendAnswerRequestAsync(question, searchResult.Matches, conversation, stream: true, cancellationToken);
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
        IReadOnlyList<ChatHistoryMessage> conversation,
        bool stream,
        CancellationToken cancellationToken)
    {
        var contextMatches = SelectContextMatches(matches, maxMatches: 6, maxMatchesPerDocument: 3);
        var context = string.Join("\n\n", contextMatches.Select((match, index) =>
            $"[S{index + 1}] Документ: {match.DocumentTitle}\nРаздел: {match.SourceLabel}\n{match.Text}"));
        var hasSources = contextMatches.Count > 0;
        var messages = new List<InferenceMessage>
        {
            new("system", ChatPrompt.BuildSystemMessage(context, hasSources, Array.Empty<string>()))
        };
        if (!hasSources)
        {
            messages.AddRange(conversation
                .Where(message => message.Role is "user" or "assistant")
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .TakeLast(6)
                .Select(message => new InferenceMessage(message.Role, Truncate(message.Content.Trim(), 1_000))));
        }
        messages.Add(new InferenceMessage("user", question));
        var token = configuration["AI:ApiToken"]
            ?? throw new InvalidOperationException("Не задан секрет AI__ApiToken.");
        var client = httpClientFactory.CreateClient("AmveraInference");
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = configuration["AI:Model"] ?? "qwen3_30b",
                messages,
                temperature = 0.2,
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
            logger.LogInformation("Начало индексации документа {FileName} (размер: {SizeKb} КБ).", Path.GetFileName(sourceFileName), file.Length / 1024);
            await vectorStore.DeleteByDocumentIdAsync(ragifyDocumentId, cancellationToken);
            await using var stream = file.OpenReadStream();
            var document = await DocumentIngestionService.CreateDefault().IngestFromStreamAsync(
                stream,
                sourceFileName,
                ragifyDocumentId,
                file.ContentType,
                metadata,
                cancellationToken);
            logger.LogInformation("Документ {FileName} обработан. Начинается векторизация...", Path.GetFileName(sourceFileName));
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

    internal static IReadOnlyList<RagMatch> SelectContextMatches(
        IReadOnlyList<RagMatch> matches,
        int maxMatches,
        int maxMatchesPerDocument)
    {
        var matchesPerDocument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<RagMatch>(maxMatches);
        foreach (var match in matches)
        {
            var selectedForDocument = matchesPerDocument.GetValueOrDefault(match.DocumentTitle);
            if (selectedForDocument >= maxMatchesPerDocument)
            {
                continue;
            }

            selected.Add(match);
            matchesPerDocument[match.DocumentTitle] = selectedForDocument + 1;
            if (selected.Count == maxMatches)
            {
                break;
            }
        }

        return selected;
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

    private static int? GetMetadataInt(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var rawValue))
        {
            return null;
        }

        if (rawValue is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var jsonValue))
        {
            return jsonValue;
        }

        return int.TryParse(rawValue?.ToString(), out var value) ? value : null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

internal sealed record RagMatch(string DocumentTitle, string SourceLabel, string Text, double Similarity, double RankingScore);

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
