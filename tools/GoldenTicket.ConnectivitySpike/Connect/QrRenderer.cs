using System.Globalization;
using System.Text;

namespace GoldenTicket.ConnectivitySpike.Connect;

/// <summary>
/// Puts a generated symbol somewhere a phone camera can see it: the laptop console, or an SVG file
/// for when the console font or window makes the printed one too small to scan.
/// </summary>
internal static class QrRenderer
{
    /// <summary>The standard's minimum light margin. A symbol printed without it often will not scan.</summary>
    private const int QuietZone = 4;

    /// <summary>
    /// Prints the symbol with two module rows per text line, which is close to square in a console
    /// whose characters are about twice as tall as they are wide.
    ///
    /// The colours are set explicitly: on a dark console a symbol drawn in the default foreground is
    /// inverted, and most scanners will not read it.
    /// </summary>
    internal static bool TryWriteToConsole(QrCode code, TextWriter output)
    {
        var span = code.Size + (QuietZone * 2);
        var lines = new string[(span + 1) / 2];

        for (var line = 0; line < lines.Length; line++)
        {
            var builder = new StringBuilder(span);
            for (var column = 0; column < span; column++)
            {
                var x = column - QuietZone;
                var upper = code[x, (line * 2) - QuietZone];
                var lower = code[x, (line * 2) + 1 - QuietZone];

                builder.Append((upper, lower) switch
                {
                    (true, true) => '█',     // full block
                    (true, false) => '▀',    // upper half block
                    (false, true) => '▄',    // lower half block
                    _ => ' ',
                });
            }

            lines[line] = builder.ToString();
        }

        ConsoleColor previousForeground;
        ConsoleColor previousBackground;

        try
        {
            previousForeground = Console.ForegroundColor;
            previousBackground = Console.BackgroundColor;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
        }
        catch (IOException)
        {
            // No real console attached. The caller falls back to the SVG file and the typed address.
            return false;
        }

        try
        {
            foreach (var line in lines) output.WriteLine(line);
        }
        finally
        {
            Console.ForegroundColor = previousForeground;
            Console.BackgroundColor = previousBackground;
        }

        return true;
    }

    /// <summary>
    /// An SVG of the same symbol. Every browser opens one, and it scales to whatever size the phone
    /// needs, which the console cannot do.
    /// </summary>
    internal static string ToSvg(QrCode code, string title)
    {
        var span = code.Size + (QuietZone * 2);
        var path = new StringBuilder();

        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                if (!code[x, y]) continue;

                if (path.Length > 0) path.Append(' ');
                path.Append(CultureInfo.InvariantCulture, $"M{x + QuietZone},{y + QuietZone}h1v1h-1z");
            }
        }

        // The title is the address the symbol encodes, so an operator who opens the file can read
        // what they are about to point a camera at. It is escaped because it contains a URL.
        var safeTitle = title
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {span} {span}"
                 width="{span * 8}" height="{span * 8}" shape-rendering="crispEdges" role="img">
              <title>{safeTitle}</title>
              <rect width="{span}" height="{span}" fill="#FFFFFF"/>
              <path d="{path}" fill="#000000"/>
            </svg>

            """;
    }

    /// <summary>Writes the SVG and returns its path, or null if it could not be written.</summary>
    internal static string? TryWriteSvg(QrCode code, string title, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "connect.svg");
            File.WriteAllText(path, ToSvg(code, title), new UTF8Encoding(false));
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
