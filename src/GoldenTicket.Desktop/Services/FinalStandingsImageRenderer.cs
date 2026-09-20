using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;

namespace GoldenTicket.Desktop.Services;

internal static class FinalStandingsImageRenderer
{
    internal const int MaximumPngBytes = 16 * 1024 * 1024;

    internal static async Task<byte[]> RenderAsync(string summary, IReadOnlyList<FinalScoreRow> scores,
        ImageSource? board)
    {
        if (scores.Count is < 2 or > 5) throw new InvalidOperationException("Final standings need two to five players.");
        // Only explicitly public final results enter this tree. No window capture, private hand,
        // pairing QR, notification or desktop chrome can appear in the image.
        var frozenBoard = board?.CloneCurrentValue();
        if (frozenBoard?.CanFreeze == true) frozenBoard.Freeze();
        var scene = new FinalStandingsView
        {
            BoardImage = frozenBoard,
            DataContext = new Presentation(summary, scores.ToArray())
        };
        var width = Math.Max(1440, scores.Count * 356 + 72);
        scene.Measure(new Size(width, 1000));
        scene.Arrange(new Rect(0, 0, width, 1000));
        scene.UpdateLayout();
        await scene.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var height = Math.Ceiling(scene.PrepareForImage(width));
        if (height > 4096) throw new InvalidOperationException("These standings are too tall to send as one image.");
        scene.Measure(new Size(width, height));
        scene.Arrange(new Rect(0, 0, width, height));
        scene.UpdateLayout();
        await scene.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5),
            144, 144, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > MaximumPngBytes) throw new InvalidOperationException("The standings image is too large to send.");
        return output.ToArray();
    }

    private sealed record Presentation(string FinalSummary, IReadOnlyList<FinalScoreRow> FinalScores)
    {
        public bool CanShareFinalStandings => false;
        public ICommand? SendFinalStandingsToPhoneCommand => null;
        public ICommand? BackToMenuCommand => null;
        public string FinalStandingsShareStatus => "";
    }
}
