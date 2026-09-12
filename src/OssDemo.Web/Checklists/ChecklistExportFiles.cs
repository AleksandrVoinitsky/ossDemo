using System.IO.Compression;
using System.Security;
using System.Text;

internal sealed record ChecklistExport(byte[] Content, string ContentType, string FileName);

internal static class ChecklistExportFiles
{
    public static ChecklistExport CreateXlsx(ChecklistDetails checklist)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", Relationships("xl/workbook.xml"));
            AddEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Чек-лист" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            AddEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheet(checklist));
        }

        return new(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileName(checklist, "xlsx"));
    }

    public static ChecklistExport CreateDocx(ChecklistDetails checklist)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", Relationships("word/document.xml"));
            AddEntry(archive, "word/document.xml", BuildDocument(checklist));
        }

        return new(stream.ToArray(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileName(checklist, "docx"));
    }

    public static ChecklistExport CreatePdf(ChecklistDetails checklist)
    {
        var lines = new List<string>
        {
            $"AI OOS checklist: {Transliterate(checklist.Name)}",
            $"Facility: {Transliterate(checklist.Facility)}",
            $"Status: {(checklist.Status == "approved" ? "approved" : "draft")}",
            $"Template: {Transliterate(checklist.TemplateName)}",
            ""
        };
        foreach (var item in checklist.Items)
        {
            var value = $"{item.Position}. [{item.Section}] {item.Title} | Basis: {item.Basis} | Result: {item.Result} | Nonconformity: {item.Nonconformity} | Note: {item.Note}";
            lines.AddRange(Wrap(Transliterate(value), 92));
        }
        var pages = lines.Chunk(56).ToArray();
        var fontId = 3 + pages.Length * 2;
        var pageIds = Enumerable.Range(0, pages.Length).Select(index => 3 + index * 2).ToArray();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', pageIds.Select(value => $"{value} 0 R"))}] >>"
        };
        for (var index = 0; index < pages.Length; index++)
        {
            var pageId = pageIds[index];
            var contentId = pageId + 1;
            var content = new StringBuilder("BT /F1 8 Tf 35 810 Td 11 TL\n");
            content.Append('(').Append(EscapePdf($"Page {index + 1} of {pages.Length}")).Append(") Tj T*\n");
            foreach (var line in pages[index]) content.Append('(').Append(EscapePdf(line)).Append(") Tj T*\n");
            content.Append("ET");
            var contentText = content.ToString();
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("%PDF-1.4\n"); writer.Flush();
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            writer.Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); writer.Flush();
        }
        var xref = output.Position;
        writer.Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) writer.Write($"{offset:0000000000} 00000 n \n");
        writer.Write($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"); writer.Flush();
        return new(output.ToArray(), "application/pdf", FileName(checklist, "pdf"));
    }

    private static string BuildWorksheet(ChecklistDetails checklist)
    {
        var rows = new List<string[]>
        {
            new[] { "Чек-лист", checklist.Name, "Объект", checklist.Facility, "Статус", checklist.Status },
            new[] { "№", "Раздел", "Наименование", "Основание", "Результат", "Несоответствие", "Примечание", "Источник" }
        };
        rows.AddRange(checklist.Items.Select(item => new[] { item.Position.ToString(), item.Section, item.Title, item.Basis, item.Result, item.Nonconformity, item.Note, item.Origin }));
        var xmlRows = rows.Select((row, rowIndex) => $"<row r=\"{rowIndex + 1}\">{string.Concat(row.Select((value, column) => $"<c r=\"{Column(column)}{rowIndex + 1}\" t=\"inlineStr\"><is><t>{Xml(value)}</t></is></c>"))}</row>");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{string.Concat(xmlRows)}</sheetData></worksheet>";
    }

    private static string BuildDocument(ChecklistDetails checklist)
    {
        var paragraphs = new List<string> { checklist.Name, $"Объект: {checklist.Facility}", $"Статус: {checklist.Status}", $"Шаблон: {checklist.TemplateName}" };
        paragraphs.AddRange(checklist.Items.Select(item => $"{item.Position}. [{item.Section}] {item.Title} | Основание: {item.Basis} | Результат: {item.Result} | Примечание: {item.Note}"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{string.Concat(paragraphs.Select(value => $"<w:p><w:r><w:t xml:space=\"preserve\">{Xml(value)}</w:t></w:r></w:p>"))}</w:body></w:document>";
    }

    private static string Relationships(string target) => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{target}\"/></Relationships>";
    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;
    private static string Column(int index) => index < 26 ? ((char)('A' + index)).ToString() : $"A{(char)('A' + index - 26)}";
    private static string FileName(ChecklistDetails checklist, string extension) => $"AI-OOS-checklist-{checklist.Id:N}.{extension}";
    private static string EscapePdf(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    private static IEnumerable<string> Wrap(string value, int width)
    {
        if (value.Length == 0) { yield return string.Empty; yield break; }
        for (var index = 0; index < value.Length; index += width)
            yield return value.Substring(index, Math.Min(width, value.Length - index));
    }
    private static string Transliterate(string value)
    {
        const string source = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";
        string[] target = ["a","b","v","g","d","e","yo","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya","A","B","V","G","D","E","Yo","Zh","Z","I","Y","K","L","M","N","O","P","R","S","T","U","F","H","Ts","Ch","Sh","Sch","","Y","","E","Yu","Ya"];
        var builder = new StringBuilder();
        foreach (var character in value) { var index = source.IndexOf(character); builder.Append(index >= 0 ? target[index] : character <= 127 ? character : '?'); }
        return builder.ToString();
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }
}
