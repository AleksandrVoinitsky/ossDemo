using Npgsql;

internal static class ViolationDocumentPolicy
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".xls", ".xlsx" };
    public static bool IsAllowed(string fileName) => Extensions.Contains(Path.GetExtension(fileName));
    public static string SafeFileName(string fileName) => Path.GetFileName(fileName);
}

internal sealed class ViolationDocumentStore(IConfiguration configuration)
{
    private readonly string directory = configuration["Violations:Directory"]
        ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data", "violations") : "/data/violations");

    public async Task<IReadOnlyList<ViolationDocument>> GetAllAsync(CancellationToken ct)
    {
        await EnsureAsync(ct); await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id, original_name, content_type, byte_length, uploaded_at FROM app_violation_documents ORDER BY uploaded_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<ViolationDocument>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    public async Task<ViolationDocument?> AddAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0 || file.Length > 50 * 1024 * 1024 || !ViolationDocumentPolicy.IsAllowed(file.FileName)) return null;
        await EnsureAsync(ct); var id = Guid.NewGuid(); var name = ViolationDocumentPolicy.SafeFileName(file.FileName); var path = Path.Combine(directory, id.ToString("N") + Path.GetExtension(name).ToLowerInvariant());
        await using (var output = File.Create(path)) await file.CopyToAsync(output, ct);
        await using var connection = await OpenAsync(ct); await using var command = new NpgsqlCommand("INSERT INTO app_violation_documents (id, original_name, stored_path, content_type, byte_length) VALUES (@id,@name,@path,@type,@length) RETURNING uploaded_at", connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("path", path); command.Parameters.AddWithValue("type", string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType); command.Parameters.AddWithValue("length", file.Length);
        var uploadedAt = (DateTimeOffset)(await command.ExecuteScalarAsync(ct))!; return new(id, name, file.ContentType, file.Length, uploadedAt);
    }

    public async Task<ViolationDocumentFile?> OpenAsync(Guid id, CancellationToken ct)
    {
        await EnsureAsync(ct); await using var connection = await OpenAsync(ct); await using var command = new NpgsqlCommand("SELECT original_name, stored_path, content_type FROM app_violation_documents WHERE id=@id", connection); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null; var path = reader.GetString(1); if (!File.Exists(path)) return null;
        return new(reader.GetString(0), reader.GetString(2), path);
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory); await using var connection = await OpenAsync(ct); await using var command = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS app_violation_documents (id UUID PRIMARY KEY, original_name TEXT NOT NULL, stored_path TEXT NOT NULL, content_type TEXT NOT NULL, byte_length BIGINT NOT NULL, uploaded_at TIMESTAMPTZ NOT NULL DEFAULT now())", connection); await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")); await connection.OpenAsync(ct); return connection; }
}

internal sealed record ViolationDocument(Guid Id, string OriginalName, string ContentType, long ByteLength, DateTimeOffset UploadedAt);
internal sealed record ViolationDocumentFile(string OriginalName, string ContentType, string Path);
