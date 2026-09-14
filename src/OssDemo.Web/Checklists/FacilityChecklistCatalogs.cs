internal sealed class FacilityChecklistCatalogs
{
    public FacilityChecklistCatalogs()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "requirements");
        Requirements = RequirementCatalog.Load(Path.Combine(directory, "requirements-registry.jsonl"));
        Items = ChecklistItemCatalog.Load(Path.Combine(directory, "checklist-item-catalog.jsonl"));
    }

    public RequirementCatalog Requirements { get; }
    public IReadOnlyList<ChecklistItemCatalogItem> Items { get; }
}
