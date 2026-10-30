using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Npgsql;

internal static class BardymDemoChecklist
{
    private const string ResourceName = "OssDemo.Web.Data.bardym-checklist.xlsx";
    internal static readonly Guid ExistingChecklistId = Guid.Parse("ed3a2f07-8bd3-4408-90f5-88a9851deb6a");
    private static readonly Lazy<byte[]> Workbook = new(() =>
    {
        using var stream = typeof(BardymDemoChecklist).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Missing Bardym demo workbook.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    });
    private static readonly Lazy<IReadOnlyList<ChecklistItemDetails>> ParsedItems = new(ParseItems);
    public static IReadOnlyList<ChecklistItemDetails> Items => ParsedItems.Value;

    public static ChecklistExport Export() => new(Workbook.Value.ToArray(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "Бардымское_ЛПУ_Чек_лист_ИК_ООС_2024г_на_последний_шаг.xlsx");

    // Read the original workbook directly so the displayed items and download have one source.
    private static IReadOnlyList<ChecklistItemDetails> ParseItems()
    {
        using var stream = new MemoryStream(Workbook.Value);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument Read(string path)
        {
            using var entry = zip.GetEntry(path)!.Open();
            return XDocument.Load(entry);
        }
        var strings = Read("xl/sharedStrings.xml").Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToArray();
        var items = new List<ChecklistItemDetails>();
        var section = "";
        foreach (var row in Read("xl/worksheets/sheet1.xml").Descendants(ns + "row"))
        {
            var cells = row.Elements(ns + "c").ToDictionary(
                c => new string(((string?)c.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray()),
                c => (string?)c.Attribute("t") == "s" ? strings[int.Parse(c.Element(ns + "v")!.Value, CultureInfo.InvariantCulture)]
                    : c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Select(t => t.Value)));
            string Cell(string column) => cells.GetValueOrDefault(column, "");
            if (int.TryParse(Cell("A"), out _) && Cell("B").Length > 0)
                items.Add(new(Guid.Empty, items.Count + 1, section, Cell("B"), Cell("C"), Cell("D"), Cell("E"), Cell("F"), "template"));
            else if ((int?)row.Attribute("r") > 9 && Cell("A").Length > 0 && Cell("B").Length == 0)
                section = Cell("A");
        }
        if (items.Count != 211 || items.Any(i => i.Section.Length == 0))
            throw new InvalidDataException("Unexpected Bardym demo workbook structure.");
        return items.AsReadOnly();
    }

    // Call only for a newly created checklist, or for the explicitly named existing demo.
    public static async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using (var eligible = new NpgsqlCommand("""
            SELECT c.bardym_demo FROM app_checklists c
            JOIN app_facilities f ON f.id=c.facility_id
            WHERE c.id=@id AND f.slug IN ('bardymskoe','бардымское-лпумг') AND f.name='Бардымское ЛПУМГ'
            FOR UPDATE OF c
            """, connection, transaction))
        {
            eligible.Parameters.AddWithValue("id", id);
            if (await eligible.ExecuteScalarAsync(ct) is not false) return;
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM app_checklist_items WHERE checklist_id=@id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var item in Items)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO app_checklist_items(id,checklist_id,position,section,title,basis,result,nonconformity,note,origin)
                VALUES(@itemId,@id,@position,@section,@title,@basis,@result,@nonconformity,@note,'template')
                """, connection, transaction);
            insert.Parameters.AddWithValue("itemId", Guid.NewGuid());
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("position", item.Position);
            insert.Parameters.AddWithValue("section", item.Section);
            insert.Parameters.AddWithValue("title", item.Title);
            insert.Parameters.AddWithValue("basis", item.Basis);
            insert.Parameters.AddWithValue("result", item.Result);
            insert.Parameters.AddWithValue("nonconformity", item.Nonconformity);
            insert.Parameters.AddWithValue("note", item.Note);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var mark = new NpgsqlCommand("UPDATE app_checklists SET bardym_demo=TRUE WHERE id=@id", connection, transaction);
        mark.Parameters.AddWithValue("id", id);
        await mark.ExecuteNonQueryAsync(ct);
    }

    public static async Task MigrateExistingAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var target = new NpgsqlCommand("""
            SELECT c.id FROM app_checklists c JOIN app_facilities f ON f.id=c.facility_id
            WHERE c.id=@id AND f.slug IN ('bardymskoe','бардымское-лпумг') AND f.name='Бардымское ЛПУМГ'
                AND NOT c.bardym_demo FOR UPDATE OF c
            """, connection, transaction))
        {
            target.Parameters.AddWithValue("id", ExistingChecklistId);
            if (await target.ExecuteScalarAsync(ct) is null)
            {
                await transaction.CommitAsync(ct);
                return;
            }
        }
        await using (var backup = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_bardym_demo_backup (
                checklist_id UUID PRIMARY KEY, items JSONB NOT NULL, saved_at TIMESTAMPTZ NOT NULL DEFAULT now());
            INSERT INTO app_bardym_demo_backup(checklist_id,items)
            SELECT @id, COALESCE(jsonb_agg(to_jsonb(i) ORDER BY i.position),'[]'::jsonb)
            FROM app_checklist_items i WHERE checklist_id=@id ON CONFLICT DO NOTHING;
            """, connection, transaction))
        {
            backup.Parameters.AddWithValue("id", ExistingChecklistId);
            await backup.ExecuteNonQueryAsync(ct);
        }
        await ApplyAsync(connection, transaction, ExistingChecklistId, ct);
        await transaction.CommitAsync(ct);
    }
}
