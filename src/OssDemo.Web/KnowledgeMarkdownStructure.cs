using System.Text.RegularExpressions;

internal static partial class KnowledgeMarkdownStructure
{
    private const int MaximumChunkLength = 4_000;

    public static IReadOnlyList<KnowledgeStructuredChunk> Extract(string markdown, KnowledgeDocumentMetadata document)
    {
        var body = RemoveFrontMatter(markdown);
        var chunks = new List<KnowledgeStructuredChunk>();
        var headings = new string[6];
        var buffer = new List<string>();
        var currentClause = string.Empty;

        foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (TryGetHeading(line, out var level, out var heading))
            {
                Flush();
                headings[level - 1] = heading;
                for (var index = level; index < headings.Length; index++) headings[index] = string.Empty;
                currentClause = ExtractClause(heading);
                continue;
            }

            if (TryGetClause(line, out var clause))
            {
                Flush();
                currentClause = clause;
            }

            buffer.Add(rawLine);
            if (buffer.Sum(item => item.Length + 1) >= MaximumChunkLength) Flush();
        }

        Flush();
        return chunks;

        void Flush()
        {
            var text = string.Join('\n', buffer).Trim();
            buffer.Clear();
            if (text.Length < 20) return;

            var headingPath = string.Join(" > ", headings.Where(value => !string.IsNullOrWhiteSpace(value)));
            var heading = string.IsNullOrWhiteSpace(headingPath) ? document.Title : headingPath;
            var references = ExtractReferences($"{document.Title}\n{heading}\n{text}");
            chunks.Add(new KnowledgeStructuredChunk(heading, headingPath, currentClause, text, references));
        }
    }

    internal static IReadOnlyList<string> ExtractReferences(string value) => ReferencePattern().Matches(value)
        .Select(match => match.Value.Trim())
        .Select(NormalizeReference)
        .Where(reference => reference.Length >= 4)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal static string NormalizeReference(string value) => Regex.Replace(value, "[\\s№Nn.]", string.Empty)
        .ToUpperInvariant();

    internal static string NormalizeClause(string value) => Regex.Replace(value, "[\\s.№Nn]", string.Empty)
        .ToUpperInvariant();

    private static bool TryGetHeading(string line, out int level, out string heading)
    {
        var match = HeadingPattern().Match(line);
        level = match.Success ? match.Groups[1].Value.Length : 0;
        heading = match.Success ? match.Groups[2].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static bool TryGetClause(string line, out string clause)
    {
        var match = ClausePattern().Match(line);
        clause = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static string ExtractClause(string heading) => TryGetClause(heading, out var clause) ? clause : string.Empty;

    private static string RemoveFrontMatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal)) return markdown;
        var closingOffset = markdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        return closingOffset < 0 ? markdown : markdown[(closingOffset + 4)..];
    }

    [GeneratedRegex("(?m)^(#{1,6})\\s+(.+?)\\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex("^(Статья\\s+\\d+(?:\\.\\d+)*|Глава\\s+\\S+|Раздел\\s+\\S+|Приложение\\s+\\S+|(?:Пункт|пункт)\\s+\\d+(?:\\.\\d+)*|\\d+(?:\\.\\d+){1,4})(?:[.\\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ClausePattern();

    [GeneratedRegex("(?<![\\d-])(?:\\d{1,4}-ФЗ|(?:СТО\\s+(?:Газпром\\s+)?)?\\d+(?:[.-]\\d+){1,5}-\\d{2,4}|ГОСТ(?:\\s+Р)?\\s+\\d+(?:[.-]\\d+)*-\\d{2,4}|ISO\\s+\\d{4,5}:\\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex ReferencePattern();
}

internal sealed record KnowledgeStructuredChunk(string Heading, string HeadingPath, string Clause, string Text, IReadOnlyList<string> References);
