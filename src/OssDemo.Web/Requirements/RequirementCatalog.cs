using System.Text.Json;

internal sealed record RequirementCatalogItem(
    string Id,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> ClassifierCodes,
    string Basis,
    string Requirement,
    IReadOnlyList<string> Categories);

internal sealed class RequirementCatalog
{
    private readonly IReadOnlyDictionary<string, RequirementCatalogItem> byId;

    private RequirementCatalog(IReadOnlyList<RequirementCatalogItem> items)
    {
        Items = items;
        byId = items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RequirementCatalogItem> Items { get; }
    public int Count => Items.Count;
    public bool ContainsId(string id) => byId.ContainsKey(id);
    public RequirementCatalogItem? Find(string id) => byId.GetValueOrDefault(id);

    public static RequirementCatalog Load(string path) => new(File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<RequirementCatalogItem>(line, JsonOptions)
            ?? throw new InvalidDataException("Строка реестра требований пуста."))
        .ToArray());

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
