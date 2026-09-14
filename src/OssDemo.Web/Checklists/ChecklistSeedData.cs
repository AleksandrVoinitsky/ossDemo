internal static class ChecklistSeedData
{
    private static readonly ChecklistSeedDataset Dataset = ChecklistSeedDataLoader.LoadEmbedded();

    public static readonly IReadOnlyList<ChecklistSeedTemplate> Templates = Dataset.Templates;
    public static readonly IReadOnlyList<ChecklistSeedHistory> History = Dataset.History;
}
