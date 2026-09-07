using System.Text.Json;

internal sealed class ChecklistService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ChecklistService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _sourceDirectory = Path.Combine(environment.ContentRootPath, "Data", "Checklists");
    private readonly string _workingDirectory = configuration["Checklists:Directory"] ?? "/data/checklists";

    public async Task<IReadOnlyList<ChecklistCatalogEntry>> GetCatalogAsync(CancellationToken cancellationToken) =>
        await ReadJsonAsync<List<ChecklistCatalogEntry>>(Path.Combine(_sourceDirectory, "catalog.json"), cancellationToken) ?? [];

    public async Task<ChecklistSource?> GetSourceAsync(string id, CancellationToken cancellationToken)
    {
        var entry = (await GetCatalogAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
        if (entry is null) return null;

        var filePath = Path.Combine(_sourceDirectory, entry.FileName);
        if (!File.Exists(filePath))
        {
            logger.LogWarning("Не найден файл исходных данных чек-листа {ChecklistId}: {FilePath}", id, filePath);
            return null;
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        return new ChecklistSource(entry, ParseCsv(lines).ToArray());
    }

    public async Task<WorkingChecklist?> CreateAsync(CreateChecklistRequest request, CancellationToken cancellationToken)
    {
        var source = await GetSourceAsync(request.TemplateId, cancellationToken);
        if (source?.Catalog.Kind != "template") return null;

        var checklist = new WorkingChecklist(
            Guid.NewGuid(),
            request.Name?.Trim() is { Length: > 0 } name ? name : $"Чек-лист ООС — {source.Catalog.Facility}",
            request.Facility?.Trim() is { Length: > 0 } facility ? facility : source.Catalog.Facility,
            source.Catalog.Id,
            DateTimeOffset.UtcNow,
            "draft",
            source.Items.Select(item => new WorkingChecklistItem(Guid.NewGuid(), item.Number, item.Section, item.Title, item.Basis, item.Result, item.Nonconformity, item.Note, "template")).ToList());
        await SaveAsync(checklist, cancellationToken);
        return checklist;
    }

    public async Task<WorkingChecklist?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await ReadJsonAsync<WorkingChecklist>(Path.Combine(_workingDirectory, $"{id:N}.json"), cancellationToken);

    public async Task<WorkingChecklist?> AddItemAsync(Guid id, AddChecklistItemRequest request, CancellationToken cancellationToken)
    {
        var checklist = await GetAsync(id, cancellationToken);
        if (checklist is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Basis)) return null;
        var nextNumber = checklist.Items.Count == 0 ? 1 : checklist.Items.Max(item => item.Number) + 1;
        checklist.Items.Add(new WorkingChecklistItem(Guid.NewGuid(), nextNumber, request.Section?.Trim() ?? "Ручной пункт", request.Title.Trim(), request.Basis.Trim(), "", "", request.Note?.Trim() ?? "Добавлено инспектором", "manual"));
        await SaveAsync(checklist, cancellationToken);
        return checklist;
    }

    private async Task SaveAsync(WorkingChecklist checklist, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workingDirectory);
        var path = Path.Combine(_workingDirectory, $"{checklist.Id:N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(checklist, JsonOptions), cancellationToken);
    }

    private static IEnumerable<ChecklistSourceItem> ParseCsv(IEnumerable<string> lines)
    {
        foreach (var fields in lines.Skip(1).Select(ParseCsvLine))
        {
            if (fields.Count < 7 || !int.TryParse(fields[0], out var number)) continue;
            yield return new ChecklistSourceItem(number, fields[1], fields[2], fields[3], fields[4], fields[5], fields[6]);
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        values.Add(value.ToString());
        return values;
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }
}

internal sealed record ChecklistCatalogEntry(string Id, string FileName, string Facility, int Year, string Period, string Kind, string Title);
internal sealed record ChecklistSource(ChecklistCatalogEntry Catalog, IReadOnlyList<ChecklistSourceItem> Items);
internal sealed record ChecklistSourceItem(int Number, string Section, string Title, string Basis, string Result, string Nonconformity, string Note);
internal sealed record CreateChecklistRequest(string TemplateId, string? Name, string? Facility);
internal sealed record AddChecklistItemRequest(string? Title, string? Basis, string? Section, string? Note);
internal sealed record WorkingChecklist(Guid Id, string Name, string Facility, string TemplateId, DateTimeOffset CreatedAt, string Status, List<WorkingChecklistItem> Items);
internal sealed record WorkingChecklistItem(Guid Id, int Number, string Section, string Title, string Basis, string Result, string Nonconformity, string Note, string Origin);
