using System.Security.Cryptography;

internal static class RerankerModelCache
{
    private const string ModelFileName = "model_O1.onnx";
    private const string TokenizerFileName = "tokenizer.json";
    private const string Revision = "1427fd652930e4ba29e8149678df786c240d8825";
    private const string ModelHash = "6230c9f55c7126a852c27655bdaf63df6f039fdb4e070ad3a73bab924dfc61ef";
    private const string TokenizerHash = "62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626";
    private const string RepositoryUrl = "https://huggingface.co/cross-encoder/mmarco-mMiniLMv2-L12-H384-v1/resolve/" + Revision + "/";

    public static async Task<RerankerModelPaths> EnsureAsync(IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(configuration["Embeddings:CacheDirectory"] ?? "/data/ragify-model", "reranker");
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, ModelFileName);
        var tokenizerPath = Path.Combine(directory, TokenizerFileName);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await EnsureFileAsync(client, modelPath, RepositoryUrl + "onnx/" + ModelFileName, ModelHash, "ONNX-reranker", logger, cancellationToken);
        await EnsureFileAsync(client, tokenizerPath, RepositoryUrl + TokenizerFileName, TokenizerHash, "токенизатор reranker", logger, cancellationToken);
        return new(modelPath, tokenizerPath);
    }

    public static RerankerModelCacheStatus GetStatus(IConfiguration configuration)
    {
        var directory = Path.Combine(configuration["Embeddings:CacheDirectory"] ?? "/data/ragify-model", "reranker");
        var model = new FileInfo(Path.Combine(directory, ModelFileName));
        return new(model.Exists, model.Exists ? model.Length : 0);
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
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record RerankerModelPaths(string ModelPath, string TokenizerPath);
internal sealed record RerankerModelCacheStatus(bool ModelCached, long ModelSizeBytes);
