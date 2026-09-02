using System.Collections.Concurrent;

internal sealed class RagDiagnostics(IConfiguration configuration)
{
    private const int MaxEntries = 50;
    private readonly ConcurrentQueue<RagDiagnosticEntry> _entries = new();
    private readonly string _logPath = Path.Combine(configuration["Ragify:DiagnosticsDirectory"] ?? "/data/ragify-diagnostics", "rag.log");

    public void Record(string level, string message)
    {
        var entry = new RagDiagnosticEntry(DateTimeOffset.UtcNow, level, message);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{entry.Timestamp:O} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Диагностика не должна останавливать импорт, если persistent volume недоступен.
        }
    }

    public RagDiagnosticsStatus GetStatus()
    {
        var entries = _entries.ToArray();
        if (entries.Length == 0 && File.Exists(_logPath))
        {
            entries = File.ReadLines(_logPath).TakeLast(MaxEntries)
                .Select(line => new RagDiagnosticEntry(DateTimeOffset.MinValue, "log", line))
                .ToArray();
        }

        return new(_logPath, entries);
    }
}

internal sealed record RagDiagnosticEntry(DateTimeOffset Timestamp, string Level, string Message);
internal sealed record RagDiagnosticsStatus(string LogPath, IReadOnlyList<RagDiagnosticEntry> Entries);
