using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

internal sealed class KnowledgeImportService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<KnowledgeImportService> logger) : BackgroundService
{
    private const long MaxFileSize = 20 * 1024 * 1024;
    private readonly SemaphoreSlim _reindexLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        await ReindexAsync(stoppingToken, force: false);
    }

    public async Task<RagReindexResult> ReindexAsync(CancellationToken cancellationToken) =>
        await ReindexAsync(cancellationToken, force: true);

    private async Task<RagReindexResult> ReindexAsync(CancellationToken cancellationToken, bool force)
    {
        await _reindexLock.WaitAsync(cancellationToken);
        try
        {
        var volumeDirectory = configuration["KnowledgeImport:Directory"] ?? "/data/inbox";
        if (!Directory.Exists(volumeDirectory))
        {
            Directory.CreateDirectory(volumeDirectory);
            logger.LogInformation("Создана папка для импорта базы знаний: {Directory}.", volumeDirectory);
        }

        var directories = new[]
        {
            volumeDirectory,
            Path.Combine(AppContext.BaseDirectory, "knowledge-inbox")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new RagReindexResult();
        if (force)
        {
            using var scope = serviceProvider.CreateScope();
            result.ClearedChunkCount = await scope.ServiceProvider.GetRequiredService<RagService>().ClearAsync(cancellationToken);
        }

        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var sourceFileName = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                    var importResult = await ImportFileAsync(path, sourceFileName, cancellationToken, force);
                    result.Add(importResult);
                }
                catch (RagIngestionException exception)
                {
                    logger.LogWarning("Файл {FileName} не импортирован: {Message}", Path.GetFileName(path), exception.Message);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Не удалось импортировать файл {FileName} из папки базы знаний.", Path.GetFileName(path));
                }
            }
        }
        return result;
        }
        finally
        {
            _reindexLock.Release();
        }
    }

    private async Task<RagImportResult> ImportFileAsync(string path, string sourceFileName, CancellationToken cancellationToken, bool force)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".pdf" or ".docx" or ".xlsx" or ".html" or ".htm" or ".md" or ".txt" or ".csv" or ".json" or ".jsonl"))
        {
            logger.LogInformation("Пропущен файл {FileName}: формат не поддерживается RAGify.", Path.GetFileName(path));
            return RagImportResult.Skipped;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0 || fileInfo.Length > MaxFileSize)
        {
            logger.LogWarning("Пропущен файл {FileName}: размер должен быть от 1 байта до 20 МБ.", fileInfo.Name);
            return RagImportResult.Skipped;
        }

        await using var stream = File.OpenRead(path);
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        stream.Position = 0;

        using var scope = serviceProvider.CreateScope();
        var ragService = scope.ServiceProvider.GetRequiredService<RagService>();
        if (!force && await ragService.IsSourceImportedAsync(sourceFileName, sourceHash, cancellationToken))
        {
            logger.LogInformation("Файл {FileName} не изменился, повторный импорт не нужен.", fileInfo.Name);
            return RagImportResult.Skipped;
        }

        var formFile = new FormFile(stream, 0, fileInfo.Length, "file", sourceFileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = ContentTypeFor(extension)
        };
        var document = await ragService.IngestAsync(formFile, sourceHash, cancellationToken);
        logger.LogInformation("Файл {FileName} импортирован в базу знаний: {ChunkCount} фрагментов.", fileInfo.Name, document.ChunkCount);
        return new(true, document.ChunkCount);
    }

    private static string ContentTypeFor(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".html" or ".htm" => "text/html",
        ".csv" => "text/csv",
        ".json" or ".jsonl" => "application/json",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        _ => "application/octet-stream"
    };
}

internal sealed class RagReindexResult
{
    public int ClearedChunkCount { get; set; }
    public int IndexedFileCount { get; private set; }
    public int IndexedChunkCount { get; private set; }
    public int SkippedFileCount { get; private set; }

    public void Add(RagImportResult result)
    {
        if (result.Indexed)
        {
            IndexedFileCount++;
            IndexedChunkCount += result.ChunkCount;
        }
        else
        {
            SkippedFileCount++;
        }
    }
}

internal sealed record RagImportResult(bool Indexed, int ChunkCount)
{
    public static RagImportResult Skipped { get; } = new(false, 0);
}
