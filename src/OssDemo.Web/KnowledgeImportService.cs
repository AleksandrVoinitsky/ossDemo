using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

internal sealed class KnowledgeImportService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<KnowledgeImportService> logger,
    RagDiagnostics diagnostics)
{
    private const long MaxFileSize = 20 * 1024 * 1024;
    private readonly SemaphoreSlim _reindexLock = new(1, 1);

    public async Task<RagReindexResult> ReindexAsync(CancellationToken cancellationToken) =>
        await ReindexAsync(cancellationToken, force: true);

    public async Task<RagReindexResult> CheckAsync(CancellationToken cancellationToken) =>
        await ReindexAsync(cancellationToken, force: false);

    private async Task<RagReindexResult> ReindexAsync(CancellationToken cancellationToken, bool force)
    {
        await _reindexLock.WaitAsync(cancellationToken);
        try
        {
        using (var scope = serviceProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RagDatabaseInitializer>().EnsureInitializedAsync(cancellationToken);
        }

        var volumeDirectory = configuration["KnowledgeImport:Directory"] ?? "/data/inbox";
        if (!Directory.Exists(volumeDirectory))
        {
            Directory.CreateDirectory(volumeDirectory);
            logger.LogInformation("Создана папка для импорта базы знаний: {Directory}.", volumeDirectory);
        }

        var directories = new[]
        {
            new KnowledgeImportSource(volumeDirectory, "volume"),
            new KnowledgeImportSource(Path.Combine(AppContext.BaseDirectory, "knowledge-base"), "repository")
        }.GroupBy(source => source.Directory, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();

        var result = new RagReindexResult();
        var existingDirectories = directories.Where(source => Directory.Exists(source.Directory)).ToArray();
        diagnostics.Record("info", $"Источники базы знаний: {string.Join(", ", directories.Select(source => $"{source.Name}: {source.Directory} ({(Directory.Exists(source.Directory) ? "доступна" : "отсутствует")})")).Replace("/app/knowledge-base", "knowledge-base")}");
        if (force)
        {
            using var scope = serviceProvider.CreateScope();
            result.ClearedChunkCount = await scope.ServiceProvider.GetRequiredService<RagService>().ClearAsync(cancellationToken);
            logger.LogInformation("Начата принудительная переиндексация RAGify. Очищено фрагментов: {ChunkCount}.", result.ClearedChunkCount);
        }

        foreach (var source in existingDirectories)
        {
            foreach (var path in Directory.EnumerateFiles(source.Directory, "*.md", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.FoundFileCount++;
                var relativePath = Path.GetRelativePath(source.Directory, path).Replace(Path.DirectorySeparatorChar, '/');
                var sourceFileName = $"{source.Name}/{relativePath}";

                try
                {
                    var importResult = await ImportFileAsync(path, sourceFileName, cancellationToken, force);
                    result.Add(importResult);
                    if (importResult.Indexed)
                    {
                        logger.LogInformation("✓ Проиндексирован: {FileName} ({ChunkCount} фрагментов)", sourceFileName, importResult.ChunkCount);
                    }
                }
                catch (RagIngestionException exception)
                {
                    logger.LogWarning("Файл {FileName} не импортирован: {Message}", Path.GetFileName(path), exception.Message);
                    result.FailedFileCount++;
                    result.FailedFiles.Add(sourceFileName);
                    diagnostics.Record("warning", $"Не импортирован {sourceFileName}: {exception.Message}");
                }
                catch (OperationCanceledException exception)
                {
                    logger.LogWarning(exception, "Импорт файла {FileName} отменён (возможно, превышено время обработки).", Path.GetFileName(path));
                    result.FailedFileCount++;
                    result.FailedFiles.Add($"{sourceFileName} (таймаут)");
                    diagnostics.Record("warning", $"Отменён импорт {sourceFileName}: таймаут или отмена операции");
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Не удалось импортировать файл {FileName} из папки базы знаний.", Path.GetFileName(path));
                    result.FailedFileCount++;
                    result.FailedFiles.Add($"{sourceFileName} ({exception.GetType().Name})");
                    diagnostics.Record("error", $"Ошибка импорта {Path.GetFileName(path)}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        if (result.FoundFileCount == 0)
        {
            diagnostics.Record("warning", "Для индексации не найдено файлов. Проверьте наличие knowledge-base в опубликованном образе и /data/inbox в volume.");
        }
        else if (result.FailedFileCount > 0)
        {
            logger.LogWarning("Индексация завершена с ошибками. Не проиндексировано файлов: {FailedCount}. Список: {FailedFiles}",
                result.FailedFileCount,
                string.Join(", ", result.FailedFiles.Take(10)) + (result.FailedFiles.Count > 10 ? $" и ещё {result.FailedFiles.Count - 10}" : ""));
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
            // RAGify хранит объекты чанков в кэше процесса. После перезапуска
            // повторный импорт восстанавливает его из постоянного индекса pgvector;
            // без этого QueryAsync отбрасывает все совпадения из БД.
            logger.LogInformation("Файл {FileName} не изменился. Восстанавливается кэш поиска RAGify.", fileInfo.Name);
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

internal sealed record KnowledgeImportSource(string Directory, string Name);

internal sealed class RagReindexResult
{
    public int ClearedChunkCount { get; set; }
    public int FoundFileCount { get; set; }
    public int IndexedFileCount { get; private set; }
    public int IndexedChunkCount { get; private set; }
    public int SkippedFileCount { get; private set; }
    public int FailedFileCount { get; set; }
    public List<string> FailedFiles { get; } = new();

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
