using System.IO;
using System.Text;
using System.Text.Json;

namespace GoldenTicket.Desktop;

/// <summary>
/// A fresh, local JSON-lines record of public board and camera decisions for this app launch.
/// Callers supply only board evidence and public operation identifiers, never private cards.
/// Logging failures must not interrupt a game.
/// </summary>
public static class BoardInteractionLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public const string FileName = "board-interactions.jsonl";
    public const string PreviousFileName = "board-interactions.previous.jsonl";
    public const long MaximumFileBytes = 64 * 1024 * 1024;

    public static string? CurrentPath { get; private set; }

    public static void Start(string diagnosticsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsDirectory);
        lock (Gate)
        {
            CloseWriter();
            try
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                var path = Path.Combine(diagnosticsDirectory, FileName);
                var previous = Path.Combine(diagnosticsDirectory, PreviousFileName);
                if (File.Exists(previous)) File.Delete(previous);
                _writer = Open(path);
                CurrentPath = path;
                WriteCore("app.launch", new { processId = Environment.ProcessId });
            }
            catch (Exception)
            {
                CloseWriter();
            }
        }
    }

    public static void Write(string eventName, object details)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;
        lock (Gate)
        {
            if (_writer is null) return;
            try { WriteCore(eventName, details); }
            catch (Exception) { CloseWriter(); }
        }
    }

    public static void Stop()
    {
        lock (Gate) CloseWriter();
    }

    private static void WriteCore(string eventName, object details)
    {
        var line = JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            eventName,
            details
        });
        if (_writer!.BaseStream.Position + Encoding.UTF8.GetByteCount(line) + 2 > MaximumFileBytes)
            Rotate();
        _writer!.WriteLine(line);
    }

    private static StreamWriter Open(string path) => new(
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete,
            4096, FileOptions.SequentialScan), new UTF8Encoding(false))
    { AutoFlush = true };

    private static void Rotate()
    {
        var path = CurrentPath!;
        _writer!.Dispose();
        _writer = null;
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, PreviousFileName), true);
        _writer = Open(path);
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            eventName = "log.rotated",
            details = new { previousFile = PreviousFileName }
        }));
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch (Exception) { /* A diagnostic file never prevents shutdown or a new launch. */ }
        _writer = null;
        CurrentPath = null;
    }
}
