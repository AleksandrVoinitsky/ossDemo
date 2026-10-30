using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

internal static class BardymDemoChecklistChecks
{
    public static void Run()
    {
        var file = BardymDemoChecklist.Export();
        Check(Convert.ToHexString(SHA256.HashData(file.Content)) == "C5CE302F6008880B1807ED36F201EA124D420EDC81D851DD0BB2DCC29085360E", "Excel must be the original file.");
        Check(file.FileName == "Бардымское_ЛПУ_Чек_лист_ИК_ООС_2024г_на_последний_шаг.xlsx", "Original download name.");
        Check(file.ContentType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "XLSX content type.");
        var items = BardymDemoChecklist.Items;
        Check(items.Count == 211, "Expected all 211 Excel items.");
        Check(items[0].Section == "Общие вопросы экологического надзора", "First section.");
        Check(items[0].Title.Contains("платежной базы"), "First item title.");
        Check(items[0].Basis.Contains("16.1"), "First item basis.");
        Check(items.Select(x => x.Position).SequenceEqual(Enumerable.Range(1, 211)), "Stable sequential order.");
        Check(items.All(x => x.Title.Length > 0 && x.Section.Length > 0), "No empty items.");
        var publishedAssembly = Environment.GetEnvironmentVariable("BARDYM_PUBLISHED_ASSEMBLY");
        if (!string.IsNullOrEmpty(publishedAssembly))
        {
            var assembly = System.Reflection.Assembly.LoadFile(Path.GetFullPath(publishedAssembly));
            using var stream = assembly.GetManifestResourceStream("OssDemo.Web.Data.bardym-checklist.xlsx")!;
            Check(Convert.ToHexString(SHA256.HashData(stream)) == Convert.ToHexString(SHA256.HashData(file.Content)), "Published workbook must match original.");
        }
    }

    public static async Task RunDatabaseAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("BARDYM_TEST_DATABASE");
        if (string.IsNullOrEmpty(connectionString)) return;
        Check(new NpgsqlConnectionStringBuilder(connectionString).Database?.StartsWith("oss_bardym_", StringComparison.Ordinal) == true,
            "Integration checks require a dedicated oss_bardym_ test database.");
        var ct = CancellationToken.None;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OssDatabase"] = connectionString,
            ["Checklists:Directory"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        }).Build();
        await new ChecklistDatabaseInitializer(config, NullLogger<ChecklistDatabaseInitializer>.Instance).EnsureInitializedAsync(ct);
        await new AiChecklistDatabaseInitializer(config).EnsureInitializedAsync(ct);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        async Task<object?> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync(ct);
        }
        var facilityId = (Guid)(await Scalar("SELECT id FROM app_facilities WHERE name='Бардымское ЛПУМГ'"))!;
        var otherId = (Guid)(await Scalar("SELECT id FROM app_facilities WHERE name='Воткинское ЛПУМГ'"))!;
        var repository = new PostgresChecklistRepository(config);
        var templateId = (await repository.ListTemplatesAsync(ct))[0].Id;
        var draft = (await repository.CreateDraftAsync(new(templateId, "Demo", facilityId, null, null), ct)).Value!;
        var ai = (await repository.CreateAiDraftAsync(new(facilityId, "Бардымское ЛПУМГ", "Demo AI",
            [new AiGeneratedDraftItem("Test", "Generated item", "Basis", "")]), ct)).Value!;
        Check(draft.BardymDemo && ai.BardymDemo, "Both creation paths must persist the demo marker.");
        Check(draft.Items.Count == 211 && ai.Items.Count == 211, "Both paths must save all workbook items.");
        var loaded = (await repository.GetChecklistAsync(ai.Id, ct))!;
        Check(loaded.Items.Select(i => i.Id).SequenceEqual(ai.Items.Select(i => i.Id)), "Reload preserves item IDs.");
        Check(loaded.Items.Select(i => (i.Section,i.Title,i.Basis,i.Result,i.Nonconformity,i.Note)).SequenceEqual(
            BardymDemoChecklist.Items.Select(i => (i.Section,i.Title,i.Basis,i.Result,i.Nonconformity,i.Note))), "All workbook fields must match.");
        Check(ChecklistExportFiles.CreateXlsx(loaded).Content.SequenceEqual(BardymDemoChecklist.Export().Content), "XLSX export dispatch.");
        var other = (await repository.CreateAiDraftAsync(new(otherId, "Воткинское ЛПУМГ", "Other",
            [new AiGeneratedDraftItem("Test", "Generated item", "Basis", "")]), ct)).Value!;
        Check(!other.BardymDemo && other.Items.Count == 1 && other.Items[0].Title == "Generated item", "Other facility is unchanged.");
        await Scalar($"""
            INSERT INTO app_checklists(id,name,facility_id,facility_name,template_name,status)
            VALUES('{BardymDemoChecklist.ExistingChecklistId}','Existing','{facilityId}','Бардымское ЛПУМГ','Test','draft');
            INSERT INTO app_checklist_items(id,checklist_id,position,section,title,basis,origin)
            VALUES(gen_random_uuid(),'{BardymDemoChecklist.ExistingChecklistId}',1,'Old','Original item','Original basis','manual');
            """);
        await BardymDemoChecklist.MigrateExistingAsync(connection, ct);
        var migrated = (await repository.GetChecklistAsync(BardymDemoChecklist.ExistingChecklistId, ct))!;
        await BardymDemoChecklist.MigrateExistingAsync(connection, ct);
        var repeated = (await repository.GetChecklistAsync(BardymDemoChecklist.ExistingChecklistId, ct))!;
        Check(migrated.Items.Count == 211 && migrated.Items.Select(i => i.Id).SequenceEqual(repeated.Items.Select(i => i.Id)), "Migration is idempotent.");
        Check((string)(await Scalar("SELECT items->0->>'title' FROM app_bardym_demo_backup"))! == "Original item", "Migration preserves original items.");
        Check(!(await repository.GetChecklistAsync(other.Id, ct))!.BardymDemo, "Migration must not touch another checklist.");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IChecklistRepository>(repository);
        builder.Services.AddSingleton<ChecklistService>();
        await using var app = builder.Build();
        app.MapChecklistApi();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var resultJson = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/api/checklists/{loaded.Id}", ct));
        Check(resultJson.RootElement.GetProperty("items").GetArrayLength() == 211, "Result page API returns all 211 items.");
        Check(!resultJson.RootElement.TryGetProperty("bardymDemo", out _), "No demo marker is exposed in the interface data.");
        using var response = await client.GetAsync($"/exports/checklists/{loaded.Id}.xlsx", ct);
        Check(response.IsSuccessStatusCode, "Existing XLSX URL succeeds.");
        Check((await response.Content.ReadAsByteArrayAsync(ct)).SequenceEqual(BardymDemoChecklist.Export().Content), "HTTP download must match original byte for byte.");
        Check(response.Content.Headers.ContentDisposition!.FileNameStar == BardymDemoChecklist.Export().FileName, "HTTP original filename.");
        Check(response.Content.Headers.ContentType!.MediaType == BardymDemoChecklist.Export().ContentType, "HTTP MIME type.");
        var normalDownload = await client.GetByteArrayAsync($"/exports/checklists/{other.Id}.xlsx", ct);
        Check(!normalDownload.SequenceEqual(BardymDemoChecklist.Export().Content), "Other facility retains generated XLSX.");
        await app.StopAsync(ct);
        Console.WriteLine("Bardym PostgreSQL checks passed.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
