using System.Text.Json;

internal static class AiChecklistOutputParser
{
    private const int MaxItems = 100;

    public static AiChecklistSynthesis Parse(string response, IReadOnlyList<AiChecklistEvidence> evidence)
    {
        var knownSources = evidence.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var payload = RemoveCodeFence(response);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var name = ReadString(root, "name") ?? "ИИ-чек-лист";
        if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return new AiChecklistSynthesis(name, []);

        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AiGeneratedChecklistItem>();
        foreach (var element in itemsElement.EnumerateArray())
        {
            var title = ReadString(element, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(title) || !titles.Add(Normalize(title))) continue;

            var sourceIds = ReadSourceIds(element)
                .Where(knownSources.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sourceIds.Length == 0) continue;

            var section = ReadString(element, "section")?.Trim();
            var reason = ReadString(element, "reason")?.Trim() ?? string.Empty;
            var confidence = ReadDouble(element, "confidence");
            items.Add(new AiGeneratedChecklistItem(
                string.IsNullOrWhiteSpace(section) ? "Общие вопросы" : section,
                title,
                reason,
                Math.Clamp(confidence, 0, 1),
                sourceIds));
            if (items.Count == MaxItems) break;
        }

        return new AiChecklistSynthesis(name.Trim(), items);
    }

    private static IEnumerable<string> ReadSourceIds(JsonElement element)
    {
        if (!element.TryGetProperty("sourceIds", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return [];
        return sources.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : 0;

    private static string RemoveCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                trimmed = trimmed[(firstLineEnd + 1)..lastFence].Trim();
        }

        var firstObject = trimmed.IndexOf('{');
        var lastObject = trimmed.LastIndexOf('}');
        return firstObject >= 0 && lastObject > firstObject
            ? trimmed[firstObject..(lastObject + 1)]
            : trimmed;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
