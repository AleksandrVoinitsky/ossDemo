using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

internal sealed class KnowledgePathMigration(IConfiguration configuration, ILogger<KnowledgePathMigration> logger)
{
    public static string Canonicalize(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var prefix = normalized.StartsWith("volume/", StringComparison.OrdinalIgnoreCase) ? "volume/"
            : normalized.StartsWith("repository/", StringComparison.OrdinalIgnoreCase) ? "repository/" : string.Empty;
        var relative = prefix.Length == 0 ? normalized : normalized[prefix.Length..];
        if (relative.StartsWith("Документы ГТЧ/", StringComparison.OrdinalIgnoreCase))
            relative = "Корпоративные документы/" + relative["Документы ГТЧ/".Length..];
        else if (relative.StartsWith("Документы ПАО/", StringComparison.OrdinalIgnoreCase))
            relative = "Корпоративные документы/" + relative["Документы ПАО/".Length..];
        else if (!relative.Contains('/') && relative.StartsWith("Реестр ", StringComparison.OrdinalIgnoreCase)
                 && relative.Contains("требован", StringComparison.OrdinalIgnoreCase))
            relative = "Прочие нормативные документы/" + relative;
        return prefix + relative;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["volume"] = configuration["KnowledgeImport:Directory"] ?? "/data/inbox",
            ["repository"] = Path.Combine(AppContext.BaseDirectory, "knowledge-base")
        };
        foreach (var root in roots.Values.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            MoveFiles(root);

        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));
        await connection.OpenAsync(cancellationToken);
        await using var list = new NpgsqlCommand("SELECT id,source_file_name,source_hash,source_path FROM knowledge_documents ORDER BY source_file_name", connection);
        var rows = new List<(Guid Id, string OldName, string Hash, string SourcePath)>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        foreach (var row in rows)
        {
            var canonical = Canonicalize(row.OldName);
            if (canonical.Equals(row.OldName, StringComparison.Ordinal)) continue;
            var slash = canonical.IndexOf('/');
            if (slash <= 0 || !roots.TryGetValue(canonical[..slash], out var root))
                throw new InvalidOperationException($"Неизвестный источник базы знаний: {row.OldName}.");
            var relative = canonical[(slash + 1)..];
            var physicalPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!physicalPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(physicalPath))
                throw new InvalidOperationException($"Перемещённый файл базы знаний не найден: {canonical}.");
            await using var stream = File.OpenRead(physicalPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(row.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Хэш перемещённого файла не совпадает с индексом: {canonical}.");

            var sourcePath = Canonicalize(row.SourcePath);
            var category = relative.StartsWith("Корпоративные документы/", StringComparison.OrdinalIgnoreCase) ? "Корпоративные документы"
                : relative.StartsWith("Региональные документы/", StringComparison.OrdinalIgnoreCase) ? "Региональные документы"
                : relative.StartsWith("Прочие нормативные документы/", StringComparison.OrdinalIgnoreCase) ? "Прочие нормативные документы" : null;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var update = new NpgsqlCommand("UPDATE knowledge_documents SET source_file_name=@newName,source_path=@sourcePath,category=COALESCE(@category,category) WHERE id=@id AND source_file_name=@oldName", connection, transaction))
            {
                update.Parameters.AddWithValue("newName", canonical);
                update.Parameters.AddWithValue("sourcePath", sourcePath);
                update.Parameters.Add(new NpgsqlParameter("category", NpgsqlDbType.Text)
                {
                    Value = (object?)category ?? DBNull.Value
                });
                update.Parameters.AddWithValue("id", row.Id);
                update.Parameters.AddWithValue("oldName", row.OldName);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException($"Путь документа изменился конкурентно: {row.OldName}.");
            }
            await using (var vectors = new NpgsqlCommand("""
                UPDATE ragify_vectors SET metadata=jsonb_set(jsonb_set(metadata,'{fileName}',to_jsonb(CAST(@fileName AS text))),'{sourcePath}',to_jsonb(CAST(@sourcePath AS text)))
                WHERE metadata->>'DocumentId'=replace(CAST(@id AS text),'-','')
                """, connection, transaction))
            {
                vectors.Parameters.AddWithValue("fileName", canonical);
                vectors.Parameters.AddWithValue("sourcePath", sourcePath);
                vectors.Parameters.AddWithValue("id", row.Id);
                await vectors.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Путь документа базы знаний обновлён без переиндексации: {OldPath} -> {NewPath}", row.OldName, canonical);
        }
    }

    private static void MoveFiles(string root)
    {
        var corporate = Path.Combine(root, "Корпоративные документы");
        var regional = Path.Combine(root, "Региональные документы");
        var other = Path.Combine(root, "Прочие нормативные документы");
        Directory.CreateDirectory(corporate);
        Directory.CreateDirectory(regional);
        Directory.CreateDirectory(other);
        foreach (var oldName in new[] { "Документы ГТЧ", "Документы ПАО" })
        {
            var source = Path.Combine(root, oldName);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                MoveOne(file, Path.Combine(corporate, relative));
            }
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
        }
        foreach (var file in Directory.EnumerateFiles(root, "Реестр *требован*.md", SearchOption.TopDirectoryOnly).ToArray())
            MoveOne(file, Path.Combine(other, Path.GetFileName(file)));
    }

    private static void MoveOne(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            var destinationHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
            if (!sourceHash.Equals(destinationHash, StringComparison.Ordinal))
                throw new IOException($"Конфликт файлов базы знаний: {destination}.");
            File.Delete(source);
            return;
        }
        File.Move(source, destination);
    }
}
