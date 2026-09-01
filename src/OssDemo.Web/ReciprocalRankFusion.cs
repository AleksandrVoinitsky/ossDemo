internal sealed record RankedChunk(
    Guid Id,
    string DocumentTitle,
    string SourceLabel,
    string Text,
    double Score,
    double SemanticSimilarity = 0,
    double LexicalScore = 0);

internal static class ReciprocalRankFusion
{
    public static IReadOnlyList<RankedChunk> Merge(IEnumerable<RankedChunk> lexical, IEnumerable<RankedChunk> semantic, int take, int rankConstant = 60)
    {
        var scores = new Dictionary<Guid, (RankedChunk Chunk, double Score)>();
        Add(lexical);
        Add(semantic);
        return scores.Values
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.DocumentTitle, StringComparer.Ordinal)
            .Take(take)
            .Select(item => item.Chunk with { Score = item.Score })
            .ToArray();

        void Add(IEnumerable<RankedChunk> source)
        {
            foreach (var (chunk, index) in source.Select((chunk, index) => (chunk, index)))
            {
                var score = 1d / (rankConstant + index + 1);
                scores[chunk.Id] = scores.TryGetValue(chunk.Id, out var current)
                    ? (current.Chunk with
                    {
                        SemanticSimilarity = Math.Max(current.Chunk.SemanticSimilarity, chunk.SemanticSimilarity),
                        LexicalScore = Math.Max(current.Chunk.LexicalScore, chunk.LexicalScore)
                    }, current.Score + score)
                    : (chunk, score);
            }
        }
    }
}
