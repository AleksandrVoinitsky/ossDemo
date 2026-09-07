using System.Text;

internal sealed record KnowledgeDocumentMetadata(
    string Title,
    string SourcePath,
    string Category,
    string DocumentType,
    string ProcessedBy)
{
    public static KnowledgeDocumentMetadata FromMarkdown(Stream stream, string fallbackPath)
    {
        if (!stream.CanSeek)
        {
            return FromFallback(fallbackPath);
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        if (!string.Equals(reader.ReadLine(), "---", StringComparison.Ordinal))
        {
            stream.Position = 0;
            return FromFallback(fallbackPath);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line && line != "---")
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            values[key] = value;
        }

        stream.Position = 0;
        return new KnowledgeDocumentMetadata(
            ValueOr(values, "title", Path.GetFileNameWithoutExtension(fallbackPath)),
            ValueOr(values, "source_path", fallbackPath),
            ValueOr(values, "category", "Без категории"),
            ValueOr(values, "document_type", "other"),
            ValueOr(values, "processed_by", "Не указан"));
    }

    private static KnowledgeDocumentMetadata FromFallback(string fallbackPath) => new(
        Path.GetFileNameWithoutExtension(fallbackPath), fallbackPath, "Без категории", "other", "Не указан");

    private static string ValueOr(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
