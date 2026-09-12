using System.IO;
using System.Security;

namespace GoldenTicket.Desktop;

/// <summary>Bounded fault metadata. Exception messages and data can contain private game state.</summary>
public static class DiagnosticLog
{
    public const int MaximumFiles = 7;
    public const long MaximumFileBytes = 64 * 1024;

    public static void Write(Exception exception, string directory, DateTimeOffset timestamp)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"error-{timestamp.UtcDateTime:yyyyMMdd}.log");
            var entry = $"{timestamp:O}  {exception.GetType().FullName}  code={exception.HResult:X8}" +
                        Environment.NewLine;

            if (!File.Exists(path) || new FileInfo(path).Length + System.Text.Encoding.UTF8.GetByteCount(entry) <= MaximumFileBytes)
                File.AppendAllText(path, entry);

            foreach (var old in Directory.EnumerateFiles(directory, "error-????????.log")
                         .OrderByDescending(Path.GetFileName).Skip(MaximumFiles))
                File.Delete(old);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Fault reporting must still work when the diagnostic directory is not writable.
        }
    }
}
