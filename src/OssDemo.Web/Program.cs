using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using RAGify;
using RAGify.Abstractions;
using RAGify.Core;
using RAGify.VectorStores;

var builder = WebApplication.CreateBuilder(args);
var modelCacheLogger = LoggerFactory.Create(logging => logging.AddConsole())
    .CreateLogger("RagifyModelCache");
var modelPath = await RagifyModelCache.EnsureAsync(builder.Configuration, modelCacheLogger, CancellationToken.None);
MultilingualCrossEncoderReranker? reranker = null;
try
{
    var rerankerPaths = await RerankerModelCache.EnsureAsync(builder.Configuration, modelCacheLogger, CancellationToken.None);
    reranker = new MultilingualCrossEncoderReranker(rerankerPaths.ModelPath, rerankerPaths.TokenizerPath);
}
catch (Exception exception)
{
    modelCacheLogger.LogWarning(exception, "Локальный cross-encoder reranker недоступен. Будет использовано нативное ранжирование RAGify.");
}

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpClient("AmveraInference", client =>
{
    client.BaseAddress = new Uri("https://inference.waw0.amvera.ru/v1/");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<IVectorStore>(serviceProvider =>
{
    var connectionString = builder.Configuration.GetConnectionString("OssDatabase")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase.");
    return new PgVectorStore(connectionString, "ragify_vectors", 384, new PgVectorStoreOptions());
});
builder.Services.AddSingleton<IEmbeddingProvider>(_ => new MultilingualMiniLmEmbeddingProvider(
    modelPath,
    RagifyModelCache.GetTokenizerPath(modelPath)));
if (reranker is not null)
{
    builder.Services.AddSingleton(reranker);
}
builder.Services.AddSingleton<IRagify>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var inferenceToken = configuration["AI:ApiToken"];
    var ragifyConfiguration = new RagifyConfig()
        .WithChunking(ChunkingStrategyType.Markdown, new ChunkingOptions
        {
            ChunkSize = 1_200,
            OverlapSize = 250,
            RespectSentenceBoundaries = true
        })
        .WithEmbeddings(serviceProvider.GetRequiredService<IEmbeddingProvider>())
        .WithVectorStore(serviceProvider.GetRequiredService<IVectorStore>())
        .WithLexicalReranker()
        .WithInMemoryEmbeddingCache(maxEntries: 10_000)
        .WithLogger(serviceProvider.GetRequiredService<ILogger<RAGify.Ragify>>());

    if (!string.IsNullOrWhiteSpace(inferenceToken))
    {
        ragifyConfiguration.WithOpenAIChat(inferenceToken, model: "qwen3_30b", baseUrl: "https://inference.waw0.amvera.ru/v1/");
    }

    return ragifyConfiguration.Build();
});
builder.Services.AddSingleton<RagService>();
builder.Services.AddSingleton<RagDatabaseInitializer>();
builder.Services.AddSingleton<RagDiagnostics>();
builder.Services.AddSingleton<KnowledgeImportService>();
builder.Services.AddSingleton<OperationalDataService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<KnowledgeImportService>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isPublicPath = path.StartsWithSegments("/Login")
        || path.StartsWithSegments("/Error")
        || path.StartsWithSegments("/css")
        || path.StartsWithSegments("/js")
        || path.StartsWithSegments("/lib")
        || path.StartsWithSegments("/favicon.ico")
        || path.StartsWithSegments("/OssDemo.Web.styles.css");

    if (!isPublicPath && context.Request.Cookies["oss.auth"] != "true")
    {
        context.Response.Redirect("/Login");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapStaticAssets();
app.MapGet("/exports/checklist.xlsx", () => ExportFiles.CreateXlsx())
   .WithName("ExportChecklistXlsx");
app.MapGet("/exports/checklist.docx", () => ExportFiles.CreateDocx())
   .WithName("ExportChecklistDocx");
app.MapGet("/exports/checklist.pdf", () => ExportFiles.CreatePdf())
   .WithName("ExportChecklistPdf");
app.MapGet("/api/rag/status", async (RagService ragService, CancellationToken cancellationToken) =>
{
    var status = await ragService.GetStatusAsync(cancellationToken);
    return Results.Ok(new
    {
        status.Ready,
        status.DatabaseConfigured,
        status.EmbeddingsConfigured,
        status.ChunkCount,
        status.DocumentCount,
        status.Documents,
        status.Model,
        status.Problem
    });
});
app.MapGet("/api/operations/dashboard", async (OperationalDataService operationalData, CancellationToken cancellationToken) =>
    Results.Ok(await operationalData.GetDashboardAsync(cancellationToken)));
app.MapGet("/api/operations/facilities", async (OperationalDataService operationalData, CancellationToken cancellationToken) =>
    Results.Ok(await operationalData.GetFacilitiesAsync(cancellationToken)));
app.MapGet("/api/operations/violations", async (OperationalDataService operationalData, CancellationToken cancellationToken) =>
    Results.Ok(await operationalData.GetViolationsAsync(cancellationToken)));
app.MapGet("/api/knowledge/documents", async (
    RagService ragService,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var inboxDirectory = Path.Combine(AppContext.BaseDirectory, "knowledge-inbox");
    if (!Directory.Exists(inboxDirectory))
    {
        logger.LogWarning("Каталог базы знаний не найден: {InboxDirectory}", inboxDirectory);
        return Results.Ok(Array.Empty<KnowledgeFileSummary>());
    }

    IReadOnlyList<KnowledgeDocumentSummary> indexedDocuments;
    try
    {
        indexedDocuments = await ragService.GetKnowledgeDocumentsAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is Npgsql.NpgsqlException or HttpRequestException or JsonException or InvalidOperationException)
    {
        logger.LogWarning(exception, "Не удалось сопоставить файлы базы знаний с индексом RAG.");
        indexedDocuments = Array.Empty<KnowledgeDocumentSummary>();
    }

    var documentsByPath = indexedDocuments
        .Where(document => !string.IsNullOrWhiteSpace(document.OriginalFileName))
        .GroupBy(document => document.OriginalFileName!.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    var documentsByTitle = indexedDocuments
        .GroupBy(document => document.Title, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

    var files = Directory.EnumerateFiles(inboxDirectory, "*.md", SearchOption.AllDirectories)
        .Select(filePath =>
        {
            var relativePath = Path.GetRelativePath(inboxDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/');
            var title = Path.GetFileNameWithoutExtension(filePath);
            documentsByPath.TryGetValue(relativePath, out var document);
            document ??= documentsByTitle.GetValueOrDefault(title);

            return new KnowledgeFileSummary(
                relativePath,
                document?.Id,
                document?.UpdatedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero),
                document?.ChunkCount ?? 0);
        })
        .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase);

    return Results.Ok(files);
});
app.MapGet("/api/knowledge/documents/{id:guid}", async (
    Guid id,
    RagService ragService,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        var document = await ragService.GetKnowledgeDocumentAsync(id, cancellationToken);
        return document is null ? Results.NotFound() : Results.Ok(document);
    }
    catch (Exception exception) when (exception is Npgsql.NpgsqlException or HttpRequestException or JsonException or InvalidOperationException)
    {
        logger.LogError(exception, "Не удалось получить документ {DocumentId} из базы знаний.", id);
        return Results.Problem(title: "База знаний временно недоступна", statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapPost("/api/rag/embedding-check", async (
    IRagify ragify,
    CancellationToken cancellationToken) =>
{
    var result = await ragify.QueryAsync("Проверка встроенной ONNX-модели.", new QueryOptions
    {
        Retrieval = new RetrievalOptions { TopK = 1, SimilarityThreshold = 0.0001 }
    }, cancellationToken);
    return Results.Ok(new { ready = true, model = RagService.Model, dimensions = 384, matchedChunks = result.Context.Count });
});
app.MapPost("/api/ai/chat", async (
    ChatRequest request,
    RagService ragService,
    OperationalDataService operationalData,
    ILogger<Program> logger,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    const int maxMessageLength = 4_000;
    const string model = "qwen3_30b";

    if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > maxMessageLength)
    {
        return Results.BadRequest(new { error = "Сообщение должно содержать от 1 до 4000 символов." });
    }

    if (string.Equals(request.Message.Trim(), "!status", StringComparison.OrdinalIgnoreCase))
    {
        var status = await ragService.GetStatusAsync(cancellationToken);
        return Results.Ok(new
        {
            answer = RagStatusFormatter.BuildAnswer(status),
            grounded = false,
            sources = status.Documents.Select(document => new
            {
                title = $"{document.Title} — {document.ChunkCount} фр.",
                kind = "classifier"
            }),
            model
        });
    }

    if (string.Equals(request.Message.Trim(), "!statusrag", StringComparison.OrdinalIgnoreCase))
    {
        var status = await ragService.GetStatusAsync(cancellationToken);
        var modelCache = RagifyModelCache.GetStatus(builder.Configuration);
        var diagnostics = app.Services.GetRequiredService<RagDiagnostics>().GetStatus();
        var rerankerCache = RerankerModelCache.GetStatus(builder.Configuration);
        logger.LogInformation(
            "Диагностика RAG: Ready={Ready}, Documents={DocumentCount}, Chunks={ChunkCount}, ModelCached={ModelCached}, TokenizerCached={TokenizerCached}.",
            status.Ready,
            status.DocumentCount,
            status.ChunkCount,
            modelCache.ModelCached,
            modelCache.TokenizerCached);
        return Results.Ok(new
        {
            answer = RagStatusFormatter.BuildDetailedAnswer(status, modelCache, rerankerCache, diagnostics),
            grounded = false,
            sources = Array.Empty<ChatSource>(),
            mode = "rag-status"
        });
    }

    if (string.Equals(request.Message.Trim(), "!reindex", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var importService = app.Services.GetRequiredService<KnowledgeImportService>();
            var result = await importService.ReindexAsync(cancellationToken);

            var failedFilesSection = result.FailedFiles.Count > 0
                ? $"\n\n### ❌ Неудачные файлы ({result.FailedFiles.Count}):\n" +
                  string.Join("\n", result.FailedFiles.Take(20).Select(f => $"- {f}")) +
                  (result.FailedFiles.Count > 20 ? $"\n\n*...и ещё {result.FailedFiles.Count - 20}*" : "")
                : "";

            return Results.Ok(new
            {
                answer = $"""
                    ## Переиндексация RAGify завершена

                    Очищено векторов: {result.ClearedChunkCount}.
                    Найдено файлов в источниках: {result.FoundFileCount}.
                    Проиндексировано файлов: {result.IndexedFileCount}.
                    Создано фрагментов: {result.IndexedChunkCount}.
                    Пропущено файлов: {result.SkippedFileCount}.
                    Ошибок импорта: {result.FailedFileCount}.{failedFilesSection}

                    Команда очистила только таблицу `ragify_vectors`. Исторические таблицы предыдущего конвейера не изменялись.
                    """,
                grounded = false,
                sources = Array.Empty<ChatSource>(),
                mode = "rag-reindex"
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось переиндексировать базу знаний RAGify.");
            return Results.Problem(
                title: "Переиндексация базы знаний не выполнена",
                detail: RagService.DescribeFailure(exception),
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    var rawQuestion = request.Message.Trim();
    if (rawQuestion.StartsWith("!add-", StringComparison.OrdinalIgnoreCase)
        || string.Equals(rawQuestion, "!help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(rawQuestion, "!commands", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var commandResult = await operationalData.ExecuteCommandAsync(rawQuestion, cancellationToken);
            if (commandResult.IsHandled)
            {
                return Results.Ok(new
                {
                    answer = commandResult.Answer,
                    grounded = false,
                    sources = Array.Empty<ChatSource>(),
                    mode = "operations-command"
                });
            }
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or InvalidOperationException)
        {
            logger.LogError(exception, "Не удалось выполнить команду рабочего реестра.");
            return Results.Problem(title: "Рабочий реестр временно недоступен", detail: "Не удалось сохранить запись в базе данных.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    if (rawQuestion.StartsWith('!'))
    {
        var showRerankedMatches = rawQuestion.StartsWith("!!", StringComparison.Ordinal);
        var debugQuery = rawQuestion[(showRerankedMatches ? 2 : 1)..].Trim();
        if (string.IsNullOrWhiteSpace(debugQuery))
        {
            return Results.BadRequest(new { error = "После ! или !! укажите текст для поиска по базе знаний." });
        }

        try
        {
            var debugResult = showRerankedMatches
                ? await ragService.SearchAsync(debugQuery, cancellationToken)
                : await ragService.SearchBeforeRerankAsync(debugQuery, cancellationToken);
            var answer = RagDebugResponse.Build(debugQuery, debugResult, showRerankedMatches);
            logger.LogInformation("Диагностика RAG: Query={Query}, Stage={Stage}, Chunks={ChunkCount}, Ambiguous={Ambiguous}.",
                debugQuery, showRerankedMatches ? "after-rerank" : "before-rerank", debugResult.Matches.Count, debugResult.IsAmbiguous);
            return Results.Ok(new
            {
                answer,
                grounded = debugResult.Matches.Count > 0 || debugResult.IsAmbiguous,
                sources = Array.Empty<ChatSource>(),
                mode = showRerankedMatches ? "rag-debug-reranked" : "rag-debug-candidates"
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось выполнить диагностический поиск RAG. Query={Query}", debugQuery);
            return Results.Problem(
                title: "Поиск по базе знаний временно недоступен",
                detail: RagService.DescribeFailure(exception),
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    try
    {
        if (request.Stream)
        {
            var streamingResult = await ragService.StartStreamingAnswerAsync(rawQuestion, request.Conversation, cancellationToken);
            var streamingSources = streamingResult.Matches.Select((match, index) => new ChatSource(
                $"[{index + 1}] {match.DocumentTitle}",
                match.Text,
                Math.Round(match.Similarity, 3),
                false,
                true,
                "source"));
            if (streamingResult.UpstreamResponse is not null)
            {
                using var upstreamResponse = streamingResult.UpstreamResponse;
                await ChatStreaming.WriteAsync(context.Response, upstreamResponse, streamingSources, true, cancellationToken);
                return Results.Empty;
            }

            return Results.Ok(new
            {
                answer = streamingResult.ImmediateAnswer,
                grounded = false,
                sources = streamingSources,
                model
            });
        }

        var result = await ragService.AnswerAsync(rawQuestion, request.Conversation, cancellationToken);
        var sources = result.Matches.Select((match, index) => new ChatSource(
            $"[{index + 1}] {match.DocumentTitle}",
            match.Text,
            Math.Round(match.Similarity, 3),
            false,
            true,
            "source"));
        return Results.Ok(new
        {
            answer = result.Answer,
            grounded = result.Matches.Count > 0,
            sources,
            model
        });
    }
    catch (InvalidOperationException exception)
    {
        logger.LogError(exception, "RAGify не смог сгенерировать ответ.");
        return Results.Problem(title: "ИИ-консультант пока не настроен", detail: "Добавьте секрет AI__ApiToken и перезапустите приложение.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        logger.LogError(exception, "RAGify не смог обработать вопрос.");
        return Results.Problem(title: "Поиск по базе знаний временно недоступен", detail: RagService.DescribeFailure(exception), statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

internal sealed record ChatRequest(string? Message, IReadOnlyList<ChatHistoryMessage>? Conversation, bool Stream = false)
{
    public IReadOnlyList<ChatHistoryMessage> Conversation { get; init; } = Conversation ?? Array.Empty<ChatHistoryMessage>();
}

internal sealed record ChatHistoryMessage(string Role, string Content);

internal sealed record KnowledgeFileSummary(
    string Path,
    Guid? Id,
    DateTimeOffset UpdatedAt,
    int ChunkCount);

internal sealed record InferenceMessage(string role, string content);
internal sealed record ChatSource(string title, string quote, double similarity, bool lexical, bool relevant, string kind);

internal static class RagDebugResponse
{
    public static string Build(string query, RagSearchResult result, bool afterRerank = false)
    {
        var stage = afterRerank
            ? "после cross-encoder rerank"
            : "после нативного поиска RAGify, до cross-encoder rerank";
        if (result.IsAmbiguous)
        {
            return $"""
                ## RAG: неоднозначные реквизиты

                **Запрос:** {query}

                {string.Join("\n", result.AmbiguousDocuments.Select((title, index) => $"[S{index + 1}] Документ: {title}"))}
                """;
        }

        if (result.Matches.Count == 0)
        {
            return $"## RAG: чанки не найдены ({stage})\n\n**Запрос:** {query}";
        }

        return $"## RAG: чанки {stage}\n\n**Запрос:** {query}\n\n" + string.Join("\n\n---\n\n", result.Matches.Select((match, index) =>
            $"[S{index + 1}] Документ: {match.DocumentTitle}\nРаздел: {match.SourceLabel}\nСходство: {match.Similarity:F3}; итоговый балл: {match.RankingScore:F3}\nТекст: {match.Text}"));
    }
}

internal static class ChatSearchQuery
{
    public static string Build(IReadOnlyList<ChatHistoryMessage> conversation, string userQuestion, int maxLength)
    {
        var priorQuestions = conversation
            .Where(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(3)
            .Select(message => message.Content.Trim())
            .ToList();

        priorQuestions.Add(userQuestion);
        var query = string.Join("\n", priorQuestions.Distinct(StringComparer.Ordinal));
        return query.Length <= maxLength ? query : query[^maxLength..];
    }
}

internal static class ChatPrompt
{
    public static string BuildSystemMessage(string context, bool hasSources, IReadOnlyList<string> ambiguousDocuments)
    {
        if (ambiguousDocuments.Count > 0)
        {
            return $"""
                Ты ИИ-консультант АИ ООС — помощник инспектора по охране окружающей среды. Отвечай по-русски, естественно и по существу.
                Пользователь указал реквизиты, которым соответствуют несколько документов базы знаний. Не выбирай документ наугад
                и не выдавай нормативный вывод. Кратко попроси уточнить тип документа, орган-издатель или дату, перечислив
                подходящие варианты. Каждое упоминание варианта сопровождай ссылкой [S1], [S2] и так далее.

                ## Возможные документы
                {string.Join("\n", ambiguousDocuments.Select((title, index) => $"[S{index + 1}] {title}"))}
                """;
        }

        return hasSources
        ? """
            Ты ИИ-консультант АИ ООС — помощник инспектора по охране окружающей среды.
            Помогай разбирать требования, готовить и проверять чек-листы, объяснять документы,
            экологические аспекты, производственный экологический контроль, отчётность и СЭМ.
            Отвечай по-русски, ясно и рабочим языком. Оформляй ответ в аккуратном Markdown:
            начни с короткого вывода (при необходимости заголовком `## Вывод`), затем используй
            содержательные заголовки `###`, маркированные или нумерованные списки. Таблицу Markdown
            используй только для сопоставления нескольких однородных фактов, сроков или действий;
            не создавай таблицу ради оформления. Важные условия можно кратко выделять блоком цитаты.
            Не повторяй в конце перечень источников: интерфейс покажет уникальные названия сам.
            Ниже приведены фрагменты проиндексированной базы знаний. Используй их как
            единственный источник фактов для этого ответа. Не выдумывай документы, статьи,
            ссылки или факты, которых нет в фрагментах, и не дополняй их знаниями из памяти.
            Каждый фактический вывод сопровождай ссылкой [S1], [S2] и так далее. Всегда дай
            максимально полезный ответ по доступным фрагментам: для запроса о документе кратко
            изложи его содержание и основные положения.

            ## Фрагменты базы знаний
            """ + context
        : """
            Ты ИИ-консультант АИ ООС — внутренний помощник инспектора по охране окружающей среды
            для подразделений ПАО «Газпром» и организаций группы. Сервис помогает готовить проверки,
            чек-листы и запросы по СЭМ, НВОС, производственному экологическому контролю, выбросам,
            сбросам, отходам, отчётности и корпоративным документам.

            Отвечай по-русски естественно, содержательно и по существу. Поддерживай обычный диалог:
            на общие вопросы давай понятный общий ответ, а на предметные — практическое пояснение.
            Не выдумывай ссылки на документы и не утверждай, что сведения взяты из базы знаний,
            если в контексте нет источников. Не начинай ответ с просьбы уточнить вопрос.
            """;
    }
}

internal static class ChatStreaming
{
    public static async Task WriteAsync(
        HttpResponse clientResponse,
        HttpResponseMessage upstreamResponse,
        IEnumerable<ChatSource> sources,
        bool grounded,
        CancellationToken cancellationToken)
    {
        clientResponse.StatusCode = StatusCodes.Status200OK;
        clientResponse.ContentType = "application/x-ndjson; charset=utf-8";
        clientResponse.Headers.CacheControl = "no-cache";
        clientResponse.Headers.Append("X-Accel-Buffering", "no");

        await WriteEventAsync(clientResponse, new { type = "sources", sources, grounded }, cancellationToken);

        try
        {
            await using var contentStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(contentStream, Encoding.UTF8);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[6..];
                if (data == "[DONE]")
                {
                    break;
                }

                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    var content = chunk.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta")
                        .TryGetProperty("content", out var contentElement)
                        ? contentElement.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(content))
                    {
                        await WriteEventAsync(clientResponse, new { type = "delta", content }, cancellationToken);
                    }
                }
                catch (JsonException)
                {
                    // Служебные или неполные события провайдера не должны завершать диалог.
                }
            }

            await WriteEventAsync(clientResponse, new { type = "done" }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteEventAsync(clientResponse, new { type = "interrupted", message = "Генерация ответа была прервана." }, cancellationToken);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, object value, CancellationToken cancellationToken)
    {
        await response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

internal static class RagStatusFormatter
{
    public static string BuildAnswer(RagStatus status)
    {
        var database = status.DatabaseConfigured ? "настроено" : "не настроено";
        var embeddings = status.EmbeddingsConfigured ? "настроено" : "не настроено";
        var state = status.Ready ? "**готова к поиску**" : "**не готова к поиску**";
        var documents = status.Documents.Count == 0
            ? "Пока нет проиндексированных документов."
            : string.Join("\n", status.Documents.Select(document =>
                $"- `{document.Title}` — статус: {document.Status}, фрагментов: {document.ChunkCount}."));
        var problem = string.IsNullOrWhiteSpace(status.Problem)
            ? string.Empty
            : $"\n### Требуется исправление\n{status.Problem}\n";

        return $"""
            ## Статус базы знаний

            Система {state}.

            | Проверка | Состояние |
            | --- | --- |
            | Подключение к PostgreSQL | {database} |
            | Встроенная ONNX-векторизация | {embeddings} |
            | Модель | `{status.Model}` |
            | Документов | {status.DocumentCount} |
            | Проиндексированных фрагментов | {status.ChunkCount} |

            ### Документы
            {documents}
            {problem}

            Команда `!status` не вызывает Qwen и не раскрывает ключи, пароли, строки подключения или внутренние адреса.
            """;
    }

    public static string BuildDetailedAnswer(RagStatus status, RagifyModelCacheStatus modelCache, RerankerModelCacheStatus rerankerCache, RagDiagnosticsStatus diagnostics)
    {
        var documents = status.Documents.Count == 0
            ? "Пока нет проиндексированных документов."
            : string.Join("\n", status.Documents.Select(document =>
                $"- `{document.Title}` — {document.ChunkCount} фрагментов."));
        var modelCacheState = modelCache.ModelCached ? "есть" : "нет";
        var tokenizerCacheState = modelCache.TokenizerCached ? "есть" : "нет";
        var diagnosticEntries = diagnostics.Entries.Count == 0
            ? "Записей пока нет."
            : string.Join("\n", diagnostics.Entries.TakeLast(12).Select(entry => $"- `{entry.Level}` {entry.Message}"));

        return $"""
            ## Расширенная диагностика RAGify

            | Параметр | Значение |
            | --- | --- |
            | Готовность поиска | {(status.Ready ? "готов" : "не готов")} |
            | PostgreSQL / pgvector | {(status.DatabaseConfigured ? "настроено" : "не настроено")} |
            | Документов в индексе | {status.DocumentCount} |
            | Чанков в `ragify_vectors` | {status.ChunkCount} |
            | ONNX-модель | `{status.Model}` |
            | Кэш ONNX в volume | {modelCacheState}, {FormatBytes(modelCache.ModelSizeBytes)} |
            | Кэш токенизатора в volume | {tokenizerCacheState}, {FormatBytes(modelCache.TokenizerSizeBytes)} |
            | Cross-encoder reranker | {(rerankerCache.ModelCached ? $"есть, {FormatBytes(rerankerCache.ModelSizeBytes)}" : "нет, используется RAGify")} |
            | Папка кэша | `{modelCache.Directory}` |
            | Нарезка | Markdown, 1200 символов, overlap 250 |
            | Реранжирование | Нативный гибридный поиск RAGify, затем local cross-encoder при доступности |

            ### Проиндексированные документы
            {documents}

            ### Последние события RAG
            {diagnosticEntries}

            Журнал в persistent volume: `{diagnostics.LogPath}`.

            {status.Problem}
            """;
    }

    private static string FormatBytes(long sizeBytes) => sizeBytes switch
    {
        <= 0 => "нет файла",
        >= 1024L * 1024 * 1024 => $"{sizeBytes / (1024d * 1024 * 1024):F2} ГБ",
        _ => $"{sizeBytes / (1024d * 1024):F1} МБ"
    };
}

internal static class ExportFiles
{
    public static IResult CreateXlsx()
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Чек-лист" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            AddEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml());
        }

        return Results.File(
            archiveStream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "AI-OOS-checklist-demo.xlsx");
    }

    public static IResult CreateDocx()
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "word/document.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:r><w:t>АИ ООС. Индивидуальный чек-лист demo</w:t></w:r></w:p>
                    <w:p><w:r><w:t>Объект: Березниковское ЛПУМГ. Статус: Готов.</w:t></w:r></w:p>
                    <w:p><w:r><w:t>Включены источники: база знаний, ОРД, реестр нарушений, предыдущий чек-лист.</w:t></w:r></w:p>
                  </w:body>
                </w:document>
                """);
        }

        return Results.File(
            archiveStream.ToArray(),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "AI-OOS-checklist-demo.docx");
    }

    public static IResult CreatePdf()
    {
        var pdf = """
            %PDF-1.4
            1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
            2 0 obj<</Type/Pages/Count 1/Kids[3 0 R]>>endobj
            3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj
            4 0 obj<</Length 132>>stream
            BT /F1 16 Tf 72 760 Td (AI OOS demo checklist) Tj 0 -28 Td (Object: Bereznikovskoe LPUMG) Tj 0 -28 Td (Status: Ready. Export stub for demo.) Tj ET
            endstream endobj
            5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj
            xref
            0 6
            0000000000 65535 f 
            0000000009 00000 n 
            0000000058 00000 n 
            0000000115 00000 n 
            0000000220 00000 n 
            0000000402 00000 n 
            trailer<</Root 1 0 R/Size 6>>
            startxref
            472
            %%EOF
            """;

        return Results.File(System.Text.Encoding.ASCII.GetBytes(pdf), "application/pdf", "AI-OOS-checklist-demo.pdf");
    }

    private static string BuildWorksheetXml()
    {
        var rows = new[]
        {
            new[] { "№", "Наименование", "Основание", "Да/Нет/Не применяется", "Примечание", "Раздел", "Статус", "Источник", "Причина включения" },
            new[] { "1", "Наличие утвержденной программы производственного экологического контроля", "ФЗ-7 ст. 67; программа ПЭК", "Да", "Проверить актуальность приказа", "1.4", "Включён", "База знаний + ОРД", "Объект НВОС I" },
            new[] { "8", "Представлены протоколы инструментального контроля выбросов", "СТО 16-005-2025 п. 4.2", "Нет", "Просроченное нарушение 2024", "2.3", "Критический", "Реестр нарушений", "Не устранено в срок" },
            new[] { "17", "Проверить устранение замечания по маркировке места накопления отходов", "Акт проверки 2025", "", "Повторяемость за 5 лет", "4.2", "Контрольный", "Реестр нарушений", "Повторное нарушение" },
            new[] { "23", "Проверить применимость лицензии на пользование недрами для скважины №3", "Скан приложения к лицензии", "", "Включено решением инспектора", "6.1", "Включён", "ОРД", "Низкое качество распознавания разрешено инспектором" }
        };

        var xmlRows = rows.Select((row, rowIndex) =>
        {
            var cells = row.Select((value, cellIndex) =>
            {
                var cellRef = $"{(char)('A' + cellIndex)}{rowIndex + 1}";
                return $"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(value)}</t></is></c>";
            });

            return $"<row r=\"{rowIndex + 1}\">{string.Concat(cells)}</row>";
        });

        return $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>{{string.Concat(xmlRows)}}</sheetData>
            </worksheet>
            """;
    }

    private static void AddEntry(System.IO.Compression.ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, System.IO.Compression.CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
        writer.Write(content.Trim());
    }
}
