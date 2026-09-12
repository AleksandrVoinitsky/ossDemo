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
        .WithInMemoryEmbeddingCache(maxEntries: 10_000)
        .WithLogger(serviceProvider.GetRequiredService<ILogger<RAGify.Ragify>>());

    if (!string.IsNullOrWhiteSpace(inferenceToken))
    {
        ragifyConfiguration.WithOpenAIChat(inferenceToken, model: "qwen3_30b", baseUrl: "https://inference.waw0.amvera.ru/v1/");
    }

    return ragifyConfiguration.Build();
});
builder.Services.AddSingleton<RagService>();
builder.Services.AddSingleton<LuceneSearchIndex>();
builder.Services.AddSingleton<RagDatabaseInitializer>();
builder.Services.AddSingleton<RagDiagnostics>();
builder.Services.AddSingleton<KnowledgeImportService>();
builder.Services.AddSingleton<OperationalDataService>();
builder.Services.AddSingleton<FacilityProfileService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<IChecklistRepository, PostgresChecklistRepository>();
builder.Services.AddSingleton<ChecklistService>();
builder.Services.AddSingleton<ChecklistDatabaseInitializer>();
builder.Services.AddSingleton<IAiChecklistKnowledgeSearch, AiChecklistKnowledgeSearch>();
builder.Services.AddSingleton<IAiChecklistSynthesisClient, AmveraAiChecklistSynthesisClient>();
builder.Services.AddSingleton<IAiChecklistFacilitySource, AiChecklistFacilitySource>();
builder.Services.AddSingleton<AiChecklistAgent>();
builder.Services.AddSingleton<IAiChecklistRunStore, PostgresAiChecklistRunStore>();
builder.Services.AddSingleton<AiChecklistDatabaseInitializer>();

var app = builder.Build();
await app.Services.GetRequiredService<ChecklistDatabaseInitializer>().EnsureInitializedAsync(CancellationToken.None);
await app.Services.GetRequiredService<AiChecklistDatabaseInitializer>().EnsureInitializedAsync(CancellationToken.None);

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
        || path.StartsWithSegments("/images")
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
app.MapGet("/api/operations/facility-profiles/{slug}", async (string slug, FacilityProfileService profileService, CancellationToken cancellationToken) =>
{
    var profile = await profileService.GetAsync(slug, cancellationToken);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});
app.MapPost("/api/operations/facility-profiles", async (FacilityProfileSaveRequest request, FacilityProfileService profileService, CancellationToken cancellationToken) =>
{
    var slug = await profileService.SaveAsync(null, request.Profile, request.Latitude, request.Longitude, cancellationToken);
    return slug is null ? Results.BadRequest(new { error = "Заполните полное и краткое наименования объекта." }) : Results.Created($"/Facilities/Card/{slug}", new { slug });
});
app.MapPut("/api/operations/facility-profiles/{slug}", async (string slug, FacilityProfileSaveRequest request, FacilityProfileService profileService, CancellationToken cancellationToken) =>
{
    var savedSlug = await profileService.SaveAsync(slug, request.Profile, request.Latitude, request.Longitude, cancellationToken);
    return savedSlug is null ? Results.BadRequest(new { error = "Заполните полное и краткое наименования объекта." }) : Results.Ok(new { slug = savedSlug });
});
app.MapGet("/api/operations/schedule", async (ScheduleService scheduleService, CancellationToken cancellationToken) =>
    Results.Ok(await scheduleService.GetAllAsync(cancellationToken)));
app.MapGet("/api/operations/schedule/{id:guid}", async (Guid id, ScheduleService scheduleService, CancellationToken cancellationToken) =>
{
    var item = await scheduleService.GetAsync(id, cancellationToken);
    return item is null ? Results.NotFound() : Results.Ok(item);
});
app.MapPost("/api/operations/schedule", async (ScheduleItemRequest request, ScheduleService scheduleService, CancellationToken cancellationToken) =>
{
    var item = await scheduleService.SaveAsync(null, request, cancellationToken);
    return item is null ? Results.BadRequest(new { error = "Заполните обязательные поля и укажите дату окончания не раньше даты начала." }) : Results.Created($"/Schedule/Event/{item.Id}", item);
});
app.MapPut("/api/operations/schedule/{id:guid}", async (Guid id, ScheduleItemRequest request, ScheduleService scheduleService, CancellationToken cancellationToken) =>
{
    if (await scheduleService.GetAsync(id, cancellationToken) is null)
    {
        return Results.NotFound();
    }
    var item = await scheduleService.SaveAsync(id, request, cancellationToken);
    return item is null ? Results.BadRequest(new { error = "Заполните обязательные поля и укажите дату окончания не раньше даты начала." }) : Results.Ok(item);
});
app.MapDelete("/api/operations/schedule/{id:guid}", async (Guid id, ScheduleService scheduleService, CancellationToken cancellationToken) =>
    await scheduleService.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/operations/violations", async (OperationalDataService operationalData, CancellationToken cancellationToken) =>
    Results.Ok(await operationalData.GetViolationsAsync(cancellationToken)));
app.MapChecklistApi();
app.MapAiChecklistApi();
app.MapGet("/api/knowledge/documents", async (
    RagService ragService,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var knowledgeBaseDirectory = Path.Combine(AppContext.BaseDirectory, "knowledge-base");
    if (!Directory.Exists(knowledgeBaseDirectory))
    {
        logger.LogWarning("Каталог базы знаний не найден: {KnowledgeBaseDirectory}", knowledgeBaseDirectory);
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

    var files = Directory.EnumerateFiles(knowledgeBaseDirectory, "*.md", SearchOption.AllDirectories)
        .Select(filePath =>
        {
            var relativePath = Path.GetRelativePath(knowledgeBaseDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/');
            var sourceFileName = $"repository/{relativePath}";
            var title = Path.GetFileNameWithoutExtension(filePath);
            documentsByPath.TryGetValue(sourceFileName, out var document);
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

    if (string.Equals(request.Message.Trim(), "!check", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var importService = app.Services.GetRequiredService<KnowledgeImportService>();
            var result = await importService.CheckAsync(cancellationToken);

            return Results.Ok(new
            {
                answer = $"""
                    ## Проверка базы знаний завершена

                    Найдено файлов в источниках: {result.FoundFileCount}.
                    Обработано файлов: {result.IndexedFileCount}.
                    Создано фрагментов: {result.IndexedChunkCount}.
                    Пропущено файлов: {result.SkippedFileCount}.
                    Ошибок импорта: {result.FailedFileCount}.

                    Проверка выполняется только по команде `!check`; при запуске приложения индексация не выполняется.
                    """,
                grounded = false,
                sources = Array.Empty<ChatSource>(),
                mode = "rag-check"
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось проверить базу знаний RAGify.");
            return Results.Problem(
                title: "Проверка базы знаний не выполнена",
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
                match.IsLexicalMatch,
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
            match.IsLexicalMatch,
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
    public static string BuildSearchRewriteMessage() => """
        Ты формируешь только поисковую формулировку для базы знаний АИ ООС.
        Исходный вопрос не дал результатов. Сразу подготовь ровно 8 разных коротких формулировок
        для поиска в нормативных, корпоративных и инспекционных документах. Используй разные
        ракурсы, лексику и более точные предметные термины: возможны название документа,
        требование, процедура, срок, роль, объект, экологический аспект или синонимы.
        Не отвечай на вопрос, не объясняй ход рассуждений, не добавляй фактов и не упоминай, что ты ИИ.
        Верни только корректный JSON-массив из 8 строк на русском языке: без Markdown, нумерации,
        вступлений и любого текста вне JSON.
        """;

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
        : throw new InvalidOperationException("Системная инструкция для ответа без источников не используется.");
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
            | Cross-encoder reranker | {(rerankerCache.ModelCached ? $"есть, {FormatBytes(rerankerCache.ModelSizeBytes)}" : "нет, используется порядок кандидатов")} |
            | Папка кэша | `{modelCache.Directory}` |
            | Нарезка | Markdown, 1200 символов, overlap 250 |
            | Лексический поиск | Локальный Lucene.NET: BM25, fuzzy-поиск, синонимы, веса полей |
            | Реранжирование | Кандидаты pgvector + Lucene.NET, затем local cross-encoder при доступности |

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
