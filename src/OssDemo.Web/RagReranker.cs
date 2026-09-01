using System.Net.Http.Json;

internal interface IRagReranker
{
    Task<IReadOnlyList<RankedChunk>?> RerankAsync(string query, IReadOnlyList<RankedChunk> candidates, CancellationToken cancellationToken);
}

// Включается только при явной настройке совместимого внутреннего endpoint; ошибки не блокируют RRF.
internal sealed class ConfiguredRagReranker(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ConfiguredRagReranker> logger) : IRagReranker
{
    public async Task<IReadOnlyList<RankedChunk>?> RerankAsync(string query, IReadOnlyList<RankedChunk> candidates, CancellationToken cancellationToken)
    {
        var endpoint = configuration["Rag:Reranker:Endpoint"];
        if (candidates.Count == 0 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return null;

        try
        {
            using var response = await httpClientFactory.CreateClient("Reranker").PostAsJsonAsync(uri, new
            {
                query,
                documents = candidates.Select(candidate => new { id = candidate.Id, text = candidate.Text })
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<RerankerResponse>(cancellationToken);
            if (payload?.Results is null) return null;
            var byId = candidates.ToDictionary(candidate => candidate.Id);
            var ranked = payload.Results
                .Where(result => byId.ContainsKey(result.Id))
                .OrderByDescending(result => result.Score)
                .Select(result => byId[result.Id] with { Score = result.Score })
                .ToArray();
            return ranked.Length == candidates.Count ? ranked : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "Reranker недоступен; использован результат RRF.");
            return null;
        }
    }

    private sealed record RerankerResponse(IReadOnlyList<RerankerItem>? Results);
    private sealed record RerankerItem(Guid Id, double Score);
}
