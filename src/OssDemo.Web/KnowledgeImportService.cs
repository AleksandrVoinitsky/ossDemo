using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

internal sealed class KnowledgeImportService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<KnowledgeImportService> logger) : BackgroundService
{
    private const long MaxFileSize = 20 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        var directory = configuration["KnowledgeImport:Directory"] ?? "/data/inbox";
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            logger.LogInformation("Создана папка для импорта базы знаний: {Directory}.", directory);
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ImportFileAsync(path, stoppingToken);
            }
            catch (RagIngestionException exception)
            {
                logger.LogWarning("Файл {FileName} не импортирован: {Message}", Path.GetFileName(path), exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Не удалось импортировать файл {FileName} из volume базы знаний.", Path.GetFileName(path));
            }
        }
    }

    private async Task ImportFileAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".pdf" and not ".docx" and not ".xlsx")
        {
            logger.LogInformation("Пропущен файл {FileName}: поддерживаются PDF, DOCX и XLSX.", Path.GetFileName(path));
            return;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0 || fileInfo.Length > MaxFileSize)
        {
            logger.LogWarning("Пропущен файл {FileName}: размер должен быть от 1 байта до 20 МБ.", fileInfo.Name);
            return;
        }

        await using var stream = File.OpenRead(path);
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        stream.Position = 0;

        using var scope = serviceProvider.CreateScope();
        var ragService = scope.ServiceProvider.GetRequiredService<RagService>();
        if (await ragService.IsSourceImportedAsync(fileInfo.Name, sourceHash, cancellationToken))
        {
            logger.LogInformation("Файл {FileName} не изменился, повторный импорт не нужен.", fileInfo.Name);
            return;
        }

        var formFile = new FormFile(stream, 0, fileInfo.Length, "file", fileInfo.Name)
        {
            Headers = new HeaderDictionary(),
            ContentType = ContentTypeFor(extension)
        };
        var document = await ragService.IngestAsync(formFile, sourceHash, cancellationToken);
        logger.LogInformation("Файл {FileName} импортирован в базу знаний: {ChunkCount} фрагментов.", fileInfo.Name, document.ChunkCount);
    }

    private static string ContentTypeFor(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };
}
