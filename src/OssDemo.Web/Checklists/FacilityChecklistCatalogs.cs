internal sealed class FacilityChecklistCatalogs
{
    public FacilityChecklistCatalogs()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "requirements");
        Requirements = RequirementCatalog.Load(Path.Combine(directory, "requirements-registry.jsonl"));
        Items = ChecklistItemCatalog.Load(Path.Combine(directory, "checklist-item-catalog.jsonl"));
        CatalogHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["classifierMapping"] = Hash(Path.Combine(directory, "classifier-mapping.jsonl")),
            ["requirementsRegistry"] = Hash(Path.Combine(directory, "requirements-registry.jsonl")),
            ["checklistItemCatalog"] = Hash(Path.Combine(directory, "checklist-item-catalog.jsonl"))
        };
    }

    public RequirementCatalog Requirements { get; }
    public IReadOnlyList<ChecklistItemCatalogItem> Items { get; }
    public IReadOnlyDictionary<string, string> CatalogHashes { get; }

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
