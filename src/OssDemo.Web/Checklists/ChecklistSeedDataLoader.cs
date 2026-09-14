using System.Text.Json;
using System.Text.Json.Serialization;

internal static class ChecklistSeedDataLoader
{
    private const string ResourceName = "OssDemo.Web.Data.checklists.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ChecklistSeedDataset LoadEmbedded()
    {
        using var stream = typeof(ChecklistSeedDataLoader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Встроенный набор начальных данных не найден: {ResourceName}.");

        return Deserialize(stream);
    }

    public static ChecklistSeedDataset Deserialize(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Deserialize(stream);
    }

    private static ChecklistSeedDataset Deserialize(Stream stream)
    {
        try
        {
            var dataset = JsonSerializer.Deserialize<ChecklistSeedDataset>(stream, SerializerOptions)
                ?? throw new InvalidDataException("Набор начальных данных чек-листов пуст.");

            if (dataset.Templates is null || dataset.History is null)
                throw new InvalidDataException("Набор начальных данных должен содержать templates и history.");

            return dataset;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Набор начальных данных чек-листов содержит некорректный JSON.", exception);
        }
    }
}
