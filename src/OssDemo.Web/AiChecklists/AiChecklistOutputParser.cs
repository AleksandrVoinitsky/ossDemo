using System.Text.Json;
using System.Text.RegularExpressions;

internal static class AiChecklistOutputParser
{
    private const int MaxItems = 100;

    public static AiChecklistSynthesis Parse(string response, IReadOnlyList<AiChecklistEvidence> evidence)
    {
        var knownSources = evidence.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var payload = RemoveCodeFence(response);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new AiChecklistSynthesis("ИИ-чек-лист", []);
        var name = ReadString(root, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "ИИ-чек-лист";
        if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return new AiChecklistSynthesis(name, []);

        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AiGeneratedChecklistItem>();
        foreach (var element in itemsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var title = ReadString(element, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var citations = ReadCitations(element, evidence, knownSources);
            if (citations.Count == 0) continue;
            if (!IsGrounded(title, citations)) continue;
            if (!titles.Add(Normalize(title))) continue;

            var section = ReadString(element, "section")?.Trim();
            var reason = ReadString(element, "reason")?.Trim() ?? string.Empty;
            var confidence = ReadDouble(element, "confidence");
            items.Add(new AiGeneratedChecklistItem(
                string.IsNullOrWhiteSpace(section) ? "Общие вопросы" : section,
                title,
                reason,
                Math.Clamp(confidence, 0, 1),
                citations));
            if (items.Count == MaxItems) break;
        }

        return new AiChecklistSynthesis(name, items);
    }

    private static IReadOnlyList<AiChecklistVerifiedCitation> ReadCitations(
        JsonElement element,
        IReadOnlyList<AiChecklistEvidence> evidence,
        IReadOnlySet<string> knownSources)
    {
        if (!element.TryGetProperty("citations", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return [];
        var evidenceById = evidence.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        return sources.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new { SourceId = ReadString(item, "sourceId")?.Trim(), Quote = ReadString(item, "quote")?.Trim() })
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceId) && knownSources.Contains(item.SourceId!) && IsExactQuote(item.Quote, evidenceById[item.SourceId!].Text))
            .DistinctBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AiChecklistVerifiedCitation(item.SourceId!, item.Quote!))
            .ToArray();
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

    private static bool IsGrounded(string title, IReadOnlyList<AiChecklistVerifiedCitation> citations)
    {
        var titleTerms = Terms(title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (titleTerms.Count == 0) return false;
        var quoteTerms = citations.SelectMany(item => Terms(item.Quote)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return titleTerms.Count(term => quoteTerms.Contains(term)) * 2 >= titleTerms.Count;
    }

    private static bool IsExactQuote(string? quote, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(quote) || quote.Trim().Length < 12) return false;
        return Normalize(sourceText).Contains(Normalize(quote), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Terms(string value)
    {
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "провер", "наличи", "требов", "соотве", "докуме", "объект", "контро", "пункт"
        };
        foreach (Match match in Regex.Matches(value, @"[\p{L}\p{Nd}]+"))
        {
            var word = match.Value.ToLowerInvariant();
            if (word.Length < 3) continue;
            var root = word[..Math.Min(6, word.Length)];
            if (!generic.Contains(root)) yield return root;
        }
    }
}
