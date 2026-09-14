using System.Text.Json;

internal sealed record ChecklistItemCatalogItem(
    string Id,
    int Position,
    string SectionCode,
    string Section,
    string Title,
    string Basis,
    IReadOnlyList<string> ClassifierCodes,
    IReadOnlyList<string> RequirementIds,
    string Status,
    string Provenance,
    double LinkScore,
    string LinkExplanation);

internal static class ChecklistItemCatalog
{
    public static IReadOnlyList<ChecklistItemCatalogItem> Load(string path) => File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<ChecklistItemCatalogItem>(line, JsonOptions)
            ?? throw new InvalidDataException("Строка каталога проверочных пунктов пуста."))
        .ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
