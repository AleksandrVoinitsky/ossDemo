internal static class AiChecklistBatchPlanner
{
    internal const int MaxEvidencePerBatch = 5;
    internal const int MaxContextCharacters = 9_000;

    public static IReadOnlyList<AiChecklistEvidence> PrepareEvidence(IReadOnlyList<AiChecklistEvidence> evidence)
    {
        var prepared = new List<AiChecklistEvidence>();
        foreach (var item in evidence)
        {
            if (item.Text.Length == 0)
            {
                prepared.Add(item);
                continue;
            }
            var part = 1;
            var offset = 0;
            while (offset < item.Text.Length)
            {
                var id = item.Text.Length <= MaxTextLength(item with { Id = item.Id }) ? item.Id : $"{item.Id}.{part}";
                var prototype = item with { Id = id, Text = string.Empty };
                var take = Math.Min(MaxTextLength(prototype), item.Text.Length - offset);
                if (take <= 0) throw new AiChecklistGenerationException("ai_context_too_large", "Метаданные источника превышают допустимый объём контекста.");
                prepared.Add(prototype with { Text = item.Text.Substring(offset, take) });
                offset += take;
                part++;
            }
        }
        return prepared;
    }

    public static IReadOnlyList<AiChecklistBatchPlan> Build(IReadOnlyList<AiChecklistEvidence> evidence)
    {
        var batches = new List<AiChecklistBatchPlan>();
        foreach (var group in evidence.GroupBy(item => item.QueryLabel, StringComparer.OrdinalIgnoreCase))
        {
            var ids = new List<string>();
            var characters = 0;
            foreach (var item in group)
            {
                var itemCharacters = AmveraAiChecklistSynthesisClient.RenderEvidence(item).Length;
                if (itemCharacters > MaxContextCharacters)
                    throw new AiChecklistGenerationException("ai_context_too_large", "Источник превышает допустимый объём тематического пакета.");
                var separatorCharacters = ids.Count == 0 ? 0 : 2;
                if (ids.Count > 0 && (ids.Count == MaxEvidencePerBatch || characters + separatorCharacters + itemCharacters > MaxContextCharacters))
                {
                    batches.Add(new(batches.Count, group.Key, ids.ToArray(), characters));
                    ids.Clear();
                    characters = 0;
                }
                ids.Add(item.Id);
                characters += (ids.Count == 1 ? 0 : 2) + itemCharacters;
            }
            if (ids.Count > 0) batches.Add(new(batches.Count, group.Key, ids.ToArray(), characters));
        }
        return batches;
    }

    private static int MaxTextLength(AiChecklistEvidence item) =>
        MaxContextCharacters - AmveraAiChecklistSynthesisClient.RenderEvidence(item with { Text = string.Empty }).Length;
}
