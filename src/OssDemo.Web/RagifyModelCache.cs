using System.Security.Cryptography;

internal static class RagifyModelCache
{
    private const string ModelFileName = "model_O1.onnx";
    private const string TokenizerFileName = "tokenizer.json";
    private const string ModelHash = "9ae4b831e992807334f18a91557661e94715f502a5c7248fb81675b08391e30f";
    private const string TokenizerHash = "2c3387be76557bd40970cec13153b3bbf80407865484b209e655e5e4729076b8";
    private const string ModelUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/e8f8c211226b894fcb81acc59f3b34ba3efd5f42/onnx/model_O1.onnx";
    private const string TokenizerUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/e8f8c211226b894fcb81acc59f3b34ba3efd5f42/tokenizer.json";

    public static async Task<string> EnsureAsync(IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        var bundledModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "paraphrase-multilingual-MiniLM-L12-v2", ModelFileName);
        var isContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!isContainer)
        {
            if (await HasExpectedHashAsync(bundledModelPath, ModelHash, cancellationToken))
            {
                return bundledModelPath;
            }
        }

        var cacheDirectory = configuration["Embeddings:CacheDirectory"] ?? "/data/ragify-model";
        var modelPath = Path.Combine(cacheDirectory, "onnx", ModelFileName);
        var tokenizerPath = Path.Combine(cacheDirectory, TokenizerFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await EnsureFileAsync(client, modelPath, ModelUrl, ModelHash, "ONNX-модель", logger, cancellationToken);
        await EnsureFileAsync(client, tokenizerPath, TokenizerUrl, TokenizerHash, "токенизатор", logger, cancellationToken);
        return modelPath;
    }

    public static RagifyModelCacheStatus GetStatus(IConfiguration configuration)
    {
        var cacheDirectory = configuration["Embeddings:CacheDirectory"] ?? "/data/ragify-model";
        var modelPath = Path.Combine(cacheDirectory, "onnx", ModelFileName);
        var tokenizerPath = Path.Combine(cacheDirectory, TokenizerFileName);
        var modelInfo = new FileInfo(modelPath);
        var tokenizerInfo = new FileInfo(tokenizerPath);
        return new(
            cacheDirectory,
            modelInfo.Exists,
            modelInfo.Exists ? modelInfo.Length : 0,
            tokenizerInfo.Exists,
            tokenizerInfo.Exists ? tokenizerInfo.Length : 0);
    }

    private static async Task EnsureFileAsync(HttpClient client, string path, string url, string expectedHash, string displayName, ILogger logger, CancellationToken cancellationToken)
    {
        if (await HasExpectedHashAsync(path, expectedHash, cancellationToken))
        {
            logger.LogInformation("{File} уже проверен в persistent volume: {Path}", displayName, path);
            return;
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.download";
        try
        {
            logger.LogInformation("Загружается {File} в persistent volume: {Path}", displayName, path);
            await using (var source = await client.GetStreamAsync(url, cancellationToken))
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!await HasExpectedHashAsync(temporaryPath, expectedHash, cancellationToken))
            {
                throw new InvalidOperationException($"Проверка SHA-256 не пройдена для файла {displayName}.");
            }

            File.Move(temporaryPath, path, overwrite: true);
            logger.LogInformation("{File} успешно сохранен и проверен: {Path}", displayName, path);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record RagifyModelCacheStatus(
    string Directory,
    bool ModelCached,
    long ModelSizeBytes,
    bool TokenizerCached,
    long TokenizerSizeBytes);
