using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpClient("AmveraInference", client =>
{
    client.BaseAddress = new Uri("https://inference.waw0.amvera.ru/");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient("Embeddings", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("Docling", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient("Reranker", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IRagReranker, ConfiguredRagReranker>();
builder.Services.AddSingleton<RagService>();
builder.Services.AddHostedService<KnowledgeImportService>();

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
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var baseUrl = configuration["Embeddings:BaseUrl"];
    var apiKey = configuration["Embeddings:ApiKey"];
    var configuredModel = configuration["Embeddings:Model"] ?? "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2";

    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) || string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.Problem(
            title: "Embedding-сервис не настроен",
            detail: "Задайте Embeddings__BaseUrl и Embeddings__ApiKey в секретах Amvera.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(serviceUri, "embed"))
    {
        Content = JsonContent.Create(new { inputs = new[] { "Проверка подключения embedding-сервиса." } })
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    try
    {
        using var response = await httpClientFactory.CreateClient("Embeddings").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Embedding-сервис вернул статус {StatusCode}.", (int)response.StatusCode);
            return Results.Problem(title: "Embedding-сервис временно недоступен", statusCode: StatusCodes.Status502BadGateway);
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = payload.RootElement;
        var embeddings = root.GetProperty("embeddings");
        var vector = embeddings.GetArrayLength() == 1 ? embeddings[0] : default;
        var dimensions = vector.ValueKind == JsonValueKind.Array ? vector.GetArrayLength() : 0;
        var validVector = dimensions == 384 && vector.EnumerateArray().All(value =>
            value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()));

        if (!validVector)
        {
            logger.LogWarning("Embedding-сервис вернул вектор неожиданной размерности или с некорректными значениями.");
            return Results.Problem(title: "Embedding-сервис вернул некорректный вектор", statusCode: StatusCodes.Status502BadGateway);
        }

        var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
        if (!string.Equals(model, configuredModel, StringComparison.Ordinal))
        {
            logger.LogWarning("Embedding-сервис вернул модель {ActualModel} вместо настроенной {ExpectedModel}.", model, configuredModel);
            return Results.Problem(title: "Embedding-сервис использует другую модель", statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new { ready = true, model, dimensions });
    }
    catch (HttpRequestException exception)
    {
        logger.LogError(exception, "Не удалось подключиться к embedding-сервису.");
        return Results.Problem(title: "Embedding-сервис временно недоступен", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (JsonException exception)
    {
        logger.LogError(exception, "Embedding-сервис вернул ответ в неожиданном формате.");
        return Results.Problem(title: "Embedding-сервис вернул некорректный ответ", statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapPost("/api/ai/chat", async (
    ChatRequest request,
    RagService ragService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<Program> logger,
    HttpResponse httpResponse,
    CancellationToken cancellationToken) =>
{
    const int maxMessageLength = 4_000;
    const int maxConversationMessages = 6;
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

    var apiToken = configuration["AI:ApiToken"];
    if (string.IsNullOrWhiteSpace(apiToken))
    {
        logger.LogError("Не настроен секрет AI__ApiToken для Amvera LLM Inference.");
        return Results.Problem(
            title: "ИИ-консультант пока не настроен",
            detail: "Добавьте секрет AI__ApiToken в переменные Amvera и перезапустите приложение.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var userQuestion = request.Message.Trim();
    var ragQuestion = ChatSearchQuery.Build(request.Conversation, userQuestion, maxMessageLength);

    RagSearchResult searchResult;
    try
    {
        searchResult = await ragService.SearchAsync(ragQuestion, cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        logger.LogWarning(exception, "Поиск по базе знаний недоступен. Продолжаем диалог без RAG-контекста.");
        searchResult = RagSearchResult.Empty;
    }

    var matches = searchResult.Matches;

    var context = string.Join("\n\n", matches.Select((match, index) =>
        $"[S{index + 1}] Документ: {match.DocumentTitle}\nРаздел: {match.SourceLabel}\nТекст: {match.Text}"));
    var messages = new List<InferenceMessage>
    {
        new("system", ChatPrompt.BuildSystemMessage(context, matches.Count > 0, searchResult.AmbiguousDocuments))
    };

    foreach (var message in request.Conversation
                 .Where(item => item.Role is "user" or "assistant")
                 .TakeLast(maxConversationMessages))
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            messages.Add(new InferenceMessage(message.Role, message.Content[..Math.Min(message.Content.Length, maxMessageLength)]));
        }
    }

    messages.Add(new InferenceMessage("user", userQuestion));

    using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, temperature = 0.3, stream = request.Stream }),
            Encoding.UTF8,
            MediaTypeNames.Application.Json)
    };
    upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

    try
    {
        using var response = await httpClientFactory.CreateClient("AmveraInference")
            .SendAsync(upstreamRequest, request.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Amvera LLM Inference вернул статус {StatusCode}.", (int)response.StatusCode);
            return Results.Problem(
                title: "Сервис ИИ временно недоступен",
                detail: "Повторите запрос позже.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var sources = matches.Count > 0
            ? matches.Select((match, index) => new ChatSource(
                $"[S{index + 1}] {match.DocumentTitle}, {match.SourceLabel}",
                match.Text,
                Math.Round(match.Similarity, 3),
                "source"))
            : searchResult.IsAmbiguous
                ? searchResult.AmbiguousDocuments.Select(title => new ChatSource(title, string.Empty, 0d, "ambiguous"))
                : Enumerable.Empty<ChatSource>();

        if (request.Stream)
        {
            await ChatStreaming.WriteAsync(httpResponse, response, sources, matches.Count > 0, cancellationToken);
            return Results.Empty;
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
        var answer = payload.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(answer))
        {
            logger.LogWarning("Amvera LLM Inference вернул пустой ответ.");
            return Results.Problem(title: "ИИ не вернул ответ", statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new { answer, grounded = matches.Count > 0, sources, model });
    }
    catch (HttpRequestException exception)
    {
        logger.LogError(exception, "Не удалось подключиться к Amvera LLM Inference.");
        return Results.Problem(title: "Сервис ИИ временно недоступен", detail: "Повторите запрос позже.", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (JsonException exception)
    {
        logger.LogError(exception, "Amvera LLM Inference вернул ответ в неожиданном формате.");
        return Results.Problem(title: "Сервис ИИ вернул некорректный ответ", statusCode: StatusCodes.Status502BadGateway);
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
internal sealed record ChatSource(string title, string quote, double similarity, string kind);

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
                Ты ИИ-консультант demo-системы АИ ООС. Отвечай по-русски, естественно, доброжелательно и по существу.
                Пользователь указал реквизиты, которым соответствуют несколько документов базы знаний. Не выбирай документ наугад
                и не выдавай нормативный вывод. Кратко попроси уточнить тип документа, орган-издатель или дату, перечислив
                подходящие варианты. Продолжай обычный диалог, если пользователь задаёт дополнительный вопрос.

                ## Возможные документы
                {string.Join("\n", ambiguousDocuments.Select(title => $"- {title}"))}
                """;
        }

        return hasSources
        ? """
            Ты ИИ-консультант demo-системы АИ ООС. Отвечай по-русски, естественно, доброжелательно и по существу.
            Ниже приведены фрагменты проиндексированной базы знаний. Используй их как
            приоритетный и проверяемый контекст. Не выдумывай документы, статьи, ссылки
            или факты, которых нет в фрагментах. Каждый вывод, основанный на базе знаний,
            сопровождай ссылкой [S1], [S2] и так далее. Если источников недостаточно,
            честно обозначь границу знания и дай полезное объяснение без выдуманных реквизитов.

            ## Фрагменты базы знаний
            """ + context
        : """
            Ты ИИ-консультант demo-системы АИ ООС. Отвечай по-русски, естественно, доброжелательно и по существу.
            В этом запросе нет подтверждающих фрагментов базы знаний. Поддерживай нормальный
            разговор: на приветствия и общие вопросы отвечай без формальных предупреждений.
            Не утверждай, что опираешься на документы базы знаний. Не выдумывай и не подтверждай
            существование конкретных организаций, филиалов, объектов, стран, регионов, ведомств,
            сайтов, реестров, документов, статей, ссылок, точных нормативных требований или
            результатов проверок. Не продолжай неподтверждённое предположение из предыдущих
            реплик как установленный факт. Если вопрос относится к конкретной организации,
            документу или объекту, прямо скажи, что в доступных источниках это не подтверждено,
            и попроси исходный документ либо идентификаторы. Если вопрос требует юридически
            значимого, экологического или нормативного вывода, объясни, что ответ носит общий
            характер, и предложи свериться с актуальным первоисточником. Если пользователь,
            вероятно, ищет документ или сведения по объекту, но данных недостаточно, задай от
            одного до трёх конкретных уточняющих вопросов вместо догадки: например, о типе и
            номере документа, дате, органе-издателе, объекте, периоде или регионе.
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
            | Embedding-сервис | {embeddings} |
            | Модель | `{status.Model}` |
            | Документов | {status.DocumentCount} |
            | Проиндексированных фрагментов | {status.ChunkCount} |

            ### Документы
            {documents}
            {problem}

            Команда `!status` не вызывает Qwen и не раскрывает ключи, пароли, строки подключения или внутренние адреса.
            """;
    }
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
