using System.Security.Cryptography;
using System.Text;
using RAGify.Abstractions;
using RAGify.Ingestion;

internal sealed class RagService(
    IRagify ragify,
    IVectorStore vectorStore,
    IConfiguration configuration,
    ILogger<RagService> logger)
{
    internal const string Model = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/model_O1.onnx";

    public async Task<RagStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("OssDatabase")))
        {
            return new(false, false, true, 0, 0, Array.Empty<RagDocumentStatus>(), Model,
                "Не задана строка подключения ConnectionStrings__OssDatabase.");
        }

        try
        {
            var documentIds = await ragify.GetIndexedDocumentsAsync(cancellationToken);
            var chunkCount = await vectorStore.GetCountAsync(cancellationToken);
            var documents = new List<RagDocumentStatus>(documentIds.Count);
            foreach (var documentId in documentIds)
            {
                var chunks = await ragify.GetChunksAsync(documentId, cancellationToken);
                documents.Add(new(documentId, "ragify", "indexed", chunks.Count));
            }

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
        var result = await ragify.QueryAsync(question, new QueryOptions
        {
            Retrieval = new RetrievalOptions
            {
                TopK = 8,
                SimilarityThreshold = 0.30,
                EnableDynamicTopK = true,
                EnableDeduplication = true
            }
        }, cancellationToken);

        var matches = result.Context
            .Select(context => new RagMatch(
                context.Source ?? "Документ",
                context.Chunk.Metadata.TryGetValue("heading", out var heading) ? heading?.ToString() ?? "Документ" : "Документ",
                context.Chunk.Text,
                context.Similarity,
                false,
                false,
                context.Similarity))
            .ToArray();
        return new(matches, false, Array.Empty<string>());
    }

    public Task<QueryResult> AnswerAsync(string question, CancellationToken cancellationToken) =>
        ragify.AnswerAsync(question, new QueryOptions
        {
            Retrieval = new RetrievalOptions
            {
                TopK = 3,
                SimilarityThreshold = 0.30,
                EnableDynamicTopK = true,
                EnableDeduplication = true
            },
            Generation = new GenerationOptions
            {
                SystemPrompt = """
                    Ты ИИ-консультант АИ ООС. Отвечай только по найденным фрагментам базы знаний.
                    Не придумывай документы, требования, статьи, сроки или факты. Если источников недостаточно,
                    прямо сообщи об этом. Каждое фактическое утверждение сопровождай ссылкой на источник в формате [1], [2].
                    """,
                Temperature = 0.2,
                MaxTokens = 500,
                IncludeCitations = true
            }
        }, cancellationToken);

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
        var indexedDocumentIds = await ragify.GetIndexedDocumentsAsync(cancellationToken);
        if (!indexedDocumentIds.Contains(documentId, StringComparer.Ordinal))
        {
            return false;
        }

        var chunks = await ragify.GetChunksAsync(documentId, cancellationToken);
        return chunks.Count > 0
            && chunks[0].Metadata.TryGetValue("sourceHash", out var indexedHash)
            && string.Equals(indexedHash?.ToString(), sourceHash, StringComparison.Ordinal);
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

    public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetKnowledgeDocumentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>(Array.Empty<KnowledgeDocumentSummary>());

    public Task<KnowledgeDocumentContent?> GetKnowledgeDocumentAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<KnowledgeDocumentContent?>(null);

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
}

internal sealed record RagMatch(string DocumentTitle, string SourceLabel, string Text, double Similarity, bool HasLexicalMatch, bool IsExactDocumentMatch, double RankingScore)
{
    public bool IsRelevant => Similarity >= 0.30;
}

internal sealed record RagSearchResult(IReadOnlyList<RagMatch> Matches, bool IsAmbiguous, IReadOnlyList<string> AmbiguousDocuments)
{
    public static RagSearchResult Empty { get; } = new(Array.Empty<RagMatch>(), false, Array.Empty<string>());
}

internal sealed record RagDocumentStatus(string Title, string SourceType, string Status, int ChunkCount);
internal sealed record KnowledgeDocumentSummary(Guid Id, string Title, string SourceType, string Status, string? OriginalFileName, DateTimeOffset UpdatedAt, long? SizeBytes, int ChunkCount);
internal sealed record KnowledgeDocumentContent(Guid Id, string Title, string SourceType, string Status, string? OriginalFileName, DateTimeOffset UpdatedAt, long? SizeBytes, string Markdown, int ChunkCount);
internal sealed class RagIngestionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed record RagStatus(bool Ready, bool DatabaseConfigured, bool EmbeddingsConfigured, int ChunkCount, int DocumentCount, IReadOnlyList<RagDocumentStatus> Documents, string Model, string? Problem);
