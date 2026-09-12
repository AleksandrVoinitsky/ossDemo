internal interface IAiChecklistKnowledgeSearch
{
    Task<IReadOnlyList<AiChecklistEvidence>> SearchAsync(
        IReadOnlyList<AiChecklistSearchQuery> queries,
        CancellationToken cancellationToken);
}

internal sealed class AiChecklistKnowledgeSearch(RagService ragService) : IAiChecklistKnowledgeSearch
{
    private const int MaxQueries = 8;
    private const int MaxEvidence = 32;

    public async Task<IReadOnlyList<AiChecklistEvidence>> SearchAsync(
        IReadOnlyList<AiChecklistSearchQuery> queries,
        CancellationToken cancellationToken)
    {
        var found = new List<(AiChecklistSearchQuery Query, RagMatch Match)>();
        foreach (var query in queries.Take(MaxQueries))
        {
            var result = await ragService.SearchAsync(query.Query, cancellationToken);
            found.AddRange(result.Matches.Select(match => (query, match)));
        }

        return found
            .OrderByDescending(item => item.Match.RankingScore)
            .DistinctBy(item => $"{item.Match.DocumentTitle}\n{item.Match.SourceLabel}\n{item.Match.Text}", StringComparer.OrdinalIgnoreCase)
            .Take(MaxEvidence)
            .Select((item, index) => new AiChecklistEvidence(
                $"S{index + 1}",
                item.Query.Label,
                item.Match.DocumentTitle,
                item.Match.SourceLabel,
                item.Match.Text,
                item.Match.RankingScore))
            .ToArray();
    }
}
