internal static class AiChecklistBatchPlanner
{
    internal const int MaxEvidencePerBatch = 5;
    internal const int MaxContextCharacters = 9_000;

    public static IReadOnlyList<AiChecklistBatchPlan> Build(IReadOnlyList<AiChecklistEvidence> evidence)
    {
        var batches = new List<AiChecklistBatchPlan>();
        foreach (var group in evidence.GroupBy(item => item.QueryLabel, StringComparer.OrdinalIgnoreCase))
        {
            var ids = new List<string>();
            var characters = 0;
            foreach (var item in group)
            {
                var itemCharacters = Math.Min(item.Text.Length, MaxContextCharacters);
                if (ids.Count > 0 && (ids.Count == MaxEvidencePerBatch || characters + itemCharacters > MaxContextCharacters))
                {
                    batches.Add(new(batches.Count, group.Key, ids.ToArray(), characters));
                    ids.Clear();
                    characters = 0;
                }
                ids.Add(item.Id);
                characters += itemCharacters;
            }
            if (ids.Count > 0) batches.Add(new(batches.Count, group.Key, ids.ToArray(), characters));
        }
        return batches;
    }
}
