using System.Text.Json;

internal sealed record InspectorChecklistTemplateItem(
    string Id,
    int Position,
    string SectionCode,
    string Section,
    string Title,
    string Basis);

internal static class InspectorChecklistTemplate
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<InspectorChecklistTemplateItem> ParseLines(IEnumerable<string> lines) => lines
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<InspectorChecklistTemplateItem>(line, JsonOptions)
            ?? throw new InvalidDataException("Строка рабочего шаблона не содержит данных."))
        .OrderBy(item => item.Position)
        .ToArray();

    public static IReadOnlyList<InspectorChecklistTemplateItem> SelectSections(
        IReadOnlyList<InspectorChecklistTemplateItem> items,
        IEnumerable<string> sectionCodes)
    {
        var selected = sectionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items.Where(item => selected.Contains(item.SectionCode)).OrderBy(item => item.Position).ToArray();
    }
}

internal interface IInspectorChecklistTemplateSource
{
    IReadOnlyList<InspectorChecklistTemplateItem> Items { get; }
}

internal sealed class JsonlInspectorChecklistTemplateSource : IInspectorChecklistTemplateSource
{
    public JsonlInspectorChecklistTemplateSource()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "requirements", "inspector-checklist-template.jsonl");
        if (!File.Exists(path)) throw new InvalidOperationException($"Не найден рабочий шаблон инспектора: {path}");
        Items = InspectorChecklistTemplate.ParseLines(File.ReadLines(path));
    }

    public IReadOnlyList<InspectorChecklistTemplateItem> Items { get; }
}
