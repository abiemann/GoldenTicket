namespace GoldenTicket.Vision;

/// <summary>
/// Fixed artwork reference in the same crop coordinates as ClassicUsRouteGeometry.
/// Saved-game photos can establish orientation without becoming the route map's origin.
/// </summary>
public static class ClassicUsBoardAlignment
{
    internal const int Width = 320;
    internal const int Height = 200;
    internal static CameraFrame ReferenceFrame { get; } = LoadReference();

    public static BoardPhotoAlignmentReference Reference { get; } = new(ReferenceFrame);

    private static CameraFrame LoadReference()
    {
        using var stream = typeof(ClassicUsBoardAlignment).Assembly.GetManifestResourceStream(
            "GoldenTicket.Vision.Calibration.classic-us-board-320x200.gray")
            ?? throw new InvalidOperationException("The classic-US board alignment reference is missing.");
        var gray = new byte[Width * Height];
        stream.ReadExactly(gray);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The classic-US board alignment reference has an invalid size.");
        var pixels = new byte[gray.Length * 4];
        for (var index = 0; index < gray.Length; index++)
        {
            pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = gray[index];
            pixels[index * 4 + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(Width, Height, pixels);
    }
}
