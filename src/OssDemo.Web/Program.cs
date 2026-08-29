using System.Net.Http.Headers;
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
app.MapPost("/api/ai/chat", async (
    ChatRequest request,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    const int maxMessageLength = 4_000;
    const int maxConversationMessages = 6;
    const string model = "qwen3_30b";

    if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > maxMessageLength)
    {
        return Results.BadRequest(new { error = "Сообщение должно содержать от 1 до 4000 символов." });
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

    var messages = new List<InferenceMessage>
    {
        new("system", "Ты ИИ-консультант demo-системы АИ ООС. Отвечай по-русски, кратко и понятно. " +
            "Сейчас это общий тест модели без подключенной базы знаний: не выдавай ответы за юридические или нормативные заключения и не придумывай источники, документы, статьи или факты.")
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

    messages.Add(new InferenceMessage("user", request.Message.Trim()));

    using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, temperature = 0.3 }),
            Encoding.UTF8,
            MediaTypeNames.Application.Json)
    };
    upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

    try
    {
        using var response = await httpClientFactory.CreateClient("AmveraInference")
            .SendAsync(upstreamRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Amvera LLM Inference вернул статус {StatusCode}.", (int)response.StatusCode);
            return Results.Problem(
                title: "Сервис ИИ временно недоступен",
                detail: "Повторите запрос позже.",
                statusCode: StatusCodes.Status502BadGateway);
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

        return Results.Ok(new { answer, sources = Array.Empty<object>(), model });
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

internal sealed record ChatRequest(string? Message, IReadOnlyList<ChatHistoryMessage>? Conversation)
{
    public IReadOnlyList<ChatHistoryMessage> Conversation { get; init; } = Conversation ?? Array.Empty<ChatHistoryMessage>();
}

internal sealed record ChatHistoryMessage(string Role, string Content);

internal sealed record InferenceMessage(string role, string content);

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
