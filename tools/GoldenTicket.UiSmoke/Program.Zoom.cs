using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Polygon = System.Windows.Shapes.Polygon;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static async Task VerifyPreviewZoom()
    {
        var allChecks = new List<object>();
        foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
        {
            BindingLog.Context = $"camera-zoom-{width}x{height}";
            await using var camera = new CameraViewModel();
            camera.Preview = SyntheticCropFixture("SYNTHETIC ZOOM FIXTURE\nNO CAMERA INPUT");
            camera.IsRunning = true;
            camera.Status = "Synthetic preview zoom and pan test. No capture device is opened.";
            NormalizedPoint[] originalCorners = [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];
            foreach (var corner in originalCorners) camera.SelectedCorners.Add(corner);
            camera.PieceOutlines =
            [
                new(false, [new(.45, .4), new(.57, .43), new(.56, .49), new(.44, .46)]),
                new(true, [new(.62, .55), new(.67, .55), new(.67, .63), new(.62, .63)])
            ];
            var fixturePixels = new byte[camera.Preview.PixelWidth * camera.Preview.PixelHeight * 4];
            camera.Preview.CopyPixels(fixturePixels, camera.Preview.PixelWidth * 4, 0);
            var referenceFrame = CameraFrame.CopyFromBgra32(camera.Preview.PixelWidth, camera.Preview.PixelHeight, fixturePixels);
            var registration = BoardRegistration.Create(referenceFrame, originalCorners);
            ZoomField<BoardRegistration?>(camera, "_registration", registration);
            camera.HasBoardCrop = true;
            var monitor = ZoomField<SceneReferenceMonitor>(camera, "_monitor");
            monitor.SetReference(referenceFrame);
            var detector = ZoomField<PieceCandidateDetector>(camera, "_pieceDetector");
            detector.SetReference(referenceFrame);
            camera.HasPieceReference = true;
            var cropRevision = ZoomField<long>(camera, "_cropRevision");
            var referenceRevision = ZoomField<long>(camera, "_referenceRevision");
            var detectorRevision = detector.ReferenceRevision;
            var sceneRevision = monitor.Current.EvidenceRevision;
            var checks = new List<string>();
            var view = new CameraView { DataContext = camera };
            var root = new Border { Width = width, Height = height, Child = view,
                Background = (Brush)System.Windows.Application.Current.Resources["Surface.Window"] };
            try
            {
                await Arrange(root, width, height);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Arrange(root, width, height);
                var area = (Grid)view.FindName("PreviewArea");
                var image = (Image)view.FindName("PreviewImage");
                var fit = ZoomRectangle(view);
                if (fit.IsEmpty || area.ActualWidth < 1 || area.ActualHeight < 1)
                    throw new InvalidOperationException("Zoom fixture requires an arranged image and preview viewport.");
                var anchor = new Point(fit.Left + fit.Width * .4, fit.Top + fit.Height * .55);
                var anchoredCoordinate = ZoomMap(view, anchor, false)
                    ?? throw new InvalidOperationException("The zoom anchor must start on the image.");
                ZoomCall(view, "SetZoom", 2.5d, anchor);
                await Arrange(root, width, height);
                var zoomed = ZoomRectangle(view);
                ZoomNear(zoomed.Width, fit.Width * 2.5, "Zoom must scale the displayed image width.");
                ZoomNear(zoomed.Height, fit.Height * 2.5, "Zoom must preserve the displayed image aspect ratio.");
                VerifyActualZoomedImage(view, zoomed);
                ZoomPoint(ZoomMap(view, anchor, false), anchoredCoordinate, "Zooming must preserve the source pixel beneath its anchor.");
                checks.Add("Zoom at a noncentral pointer anchor preserves its normalized source coordinate and image aspect ratio.");

                ZoomCall(view, "PanBy", new Vector(23, -17));
                await Arrange(root, width, height);
                var panned = ZoomRectangle(view);
                ZoomNear(panned.Left, zoomed.Left + 23, "Pan must move the image horizontally in preview pixels.");
                ZoomNear(panned.Top, zoomed.Top - 17, "Pan must move the image vertically in preview pixels.");
                VerifyActualZoomedImage(view, panned);
                var visible = new Point(area.ActualWidth / 2, area.ActualHeight / 2);
                ZoomPoint(ZoomMap(view, visible, false), new((visible.X - panned.Left) / panned.Width,
                    (visible.Y - panned.Top) / panned.Height), "Pointer mapping must use the panned and zoomed image rectangle.");
                VerifyZoomOverlay(view, camera, panned);
                checks.Add("Pan, click mapping, train/player outlines, and fixed 30-pixel corner handles share the displayed image transform.");

                // A new immutable preview is a new camera-frame presentation, not a request to fit.
                var panBeforeFrame = ZoomField<Vector>(view, "_previewPan");
                camera.Preview = SyntheticCropFixture("ANOTHER SYNTHETIC FRAME\nSAME CAMERA GEOMETRY");
                await Arrange(root, width, height);
                ZoomNear(ZoomField<double>(view, "_previewZoom"), 2.5, "Fresh preview frames must retain the user's zoom.");
                ZoomNear((ZoomField<Vector>(view, "_previewPan") - panBeforeFrame).Length, 0, "Fresh frames must retain the user's pan.");
                ZoomRect(ZoomRectangle(view), panned, "A fresh frame must not jump to a different image rectangle.");
                VerifyActualZoomedImage(view, panned);
                VerifyZoomOverlay(view, camera, panned);
                checks.Add("Replacing a preview frame with the same camera geometry retains zoom, pan, and overlay alignment.");

                ZoomCall(view, "PanBy", new Vector(1_000_000, 1_000_000));
                var upperLeft = ZoomRectangle(view);
                if (upperLeft.Left < -.01 || upperLeft.Left > 30 || upperLeft.Top < -.01 || upperLeft.Top > 30)
                    throw new InvalidOperationException("Pan must stop near the image's upper-left edges with only a bounded handle-access margin.");
                ZoomCall(view, "PanBy", new Vector(-2_000_000, -2_000_000));
                var lowerRight = ZoomRectangle(view);
                if (lowerRight.Right > area.ActualWidth + .01 || lowerRight.Right < area.ActualWidth - 30 ||
                    lowerRight.Bottom > area.ActualHeight + .01 || lowerRight.Bottom < area.ActualHeight - 30)
                    throw new InvalidOperationException("Pan must stop near the image's lower-right edges with only a bounded handle-access margin.");
                var offImage = new Point(lowerRight.Left - 100, lowerRight.Top - 100);
                if (ZoomMap(view, offImage, false) is not null)
                    throw new InvalidOperationException("An unclamped point outside the source image cannot become a crop corner.");
                ZoomPoint(ZoomMap(view, offImage, true), new(0, 0), "Dragging beyond the image must clamp to its actual edge.");
                checks.Add("Pan clamps all four image edges; pointer placement rejects out-of-image coordinates while corner drags clamp.");

                ZoomCall(view, "SetZoom", 99d, visible);
                ZoomNear(ZoomField<double>(view, "_previewZoom"), 8, "Preview zoom must have an 8× upper limit.");
                ZoomCall(view, "SetZoom", .1d, visible);
                ZoomNear(ZoomField<double>(view, "_previewZoom"), 1, "Preview zoom must not shrink below Fit.");
                ZoomRect(ZoomRectangle(view), fit, "Zooming below Fit must restore the fitted image.");
                ZoomCall(view, "SetZoom", 3d, visible);
                ZoomCall(view, "PanBy", new Vector(20, 15));
                var fitButton = (Button)view.FindName("FitPreviewButton");
                fitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Arrange(root, width, height);
                ZoomRect(ZoomRectangle(view), fit, "The actual Fit button must restore the original fitted image.");
                ZoomNear(ZoomField<Vector>(view, "_previewPan").Length, 0, "Fit must clear the presentation pan.");
                checks.Add("Zoom stays within 1×–8× and the actual Fit button restores the original image rectangle.");

                if (!camera.SelectedCorners.SequenceEqual(originalCorners) ||
                    ZoomField<long>(camera, "_cropRevision") != cropRevision ||
                    ZoomField<long>(camera, "_referenceRevision") != referenceRevision ||
                    !ReferenceEquals(ZoomField<BoardRegistration?>(camera, "_registration"), registration) ||
                    !camera.HasBoardCrop || !camera.HasPieceReference || !detector.HasReference ||
                    detector.ReferenceRevision != detectorRevision || !monitor.HasReference || monitor.Current.EvidenceRevision != sceneRevision)
                    throw new InvalidOperationException("Preview zoom, pan, Fit, and fresh frames must preserve crop geometry and both camera references.");
                checks.Add("Zoom and pan preserve stored normalized corners, crop registration/revision, and real synthetic scene/piece references.");

                var source = new FixturePresentationSource { RootVisual = view };
                void Press(Key key) => image.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                Press(Key.D1);
                var fitFirstCorner = camera.SelectedCorners[0];
                Press(Key.Right);
                var fitStep = camera.SelectedCorners[0].X - fitFirstCorner.X;
                if (fitStep <= 0) throw new InvalidOperationException("The fitted keyboard fixture must move corner one right.");
                camera.SelectedCorners[0] = fitFirstCorner;
                ZoomCall(view, "SetZoom", 4d, visible);
                ZoomCall(view, "PanBy", new Vector(-2_000_000, -2_000_000));
                Press(Key.D1);
                var selectedCorner = camera.SelectedCorners[0];
                var keyboardRectangle = ZoomRectangle(view);
                var selectedPosition = new Point(keyboardRectangle.Left + selectedCorner.X * keyboardRectangle.Width,
                    keyboardRectangle.Top + selectedCorner.Y * keyboardRectangle.Height);
                if (selectedPosition.X < 14 || selectedPosition.Y < 14 ||
                    selectedPosition.X > area.ActualWidth - 14 || selectedPosition.Y > area.ActualHeight - 14)
                    throw new InvalidOperationException("Choosing an offscreen numbered corner must bring its whole handle into view.");
                var hit = (int)(ZoomCall(view, "HitCorner", selectedPosition)
                    ?? throw new InvalidOperationException("The selected corner must remain hit-testable after zooming."));
                if (hit != 0) throw new InvalidOperationException("Corner hit testing must follow zoom and pan.");
                Press(Key.Right);
                ZoomNear(camera.SelectedCorners[0].X - selectedCorner.X, fitStep / 4,
                    "Arrow adjustment at 4× must use one quarter of its normalized Fit step.", 1e-8);
                checks.Add("Number keys reveal offscreen corner handles; hit testing follows the handle, and 4× keyboard nudges are four times finer.");

                Press(Key.D0);
                ZoomNear(ZoomField<double>(view, "_previewZoom"), 1, "The zero key must restore Fit.");
                Press(Key.Add);
                var keyZoom = ZoomField<double>(view, "_previewZoom");
                if (keyZoom <= 1) throw new InvalidOperationException("The plus key must zoom into the preview.");
                Press(Key.Subtract);
                if (ZoomField<double>(view, "_previewZoom") >= keyZoom)
                    throw new InvalidOperationException("The minus key must zoom back out.");
                checks.Add("Preview keyboard shortcuts plus, minus, and zero operate zoom and Fit without changing crop selection.");

                // Save a rendered zoomed view for visual QA, including the offscreen clipped image.
                camera.PieceOutlines =
                [
                    new(false, [new(.45, .4), new(.57, .43), new(.56, .49), new(.44, .46)]),
                    new(true, [new(.62, .55), new(.67, .55), new(.67, .63), new(.62, .63)])
                ];
                ZoomCall(view, "SetZoom", 2d, visible);
                camera.Problem = null;
                await Arrange(root, width, height);
                foreach (var scroll in Descendants<ScrollViewer>(root).Where(scroll => scroll.ScrollableHeight > 1))
                    scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + area.TranslatePoint(new Point(0, 0), scroll).Y - 80));
                await Arrange(root, width, height);
                Save(root, $"camera-zoom-{width}x{height}.png", width, height);
                camera.Preview = null;
                await Arrange(root, width, height);
                foreach (var name in new[] { "ZoomInButton", "ZoomOutButton", "FitPreviewButton", "PanPreviewToggle" })
                    if (((ButtonBase)view.FindName(name)).IsEnabled)
                        throw new InvalidOperationException($"{name} must be disabled when no preview frame is available.");
                if (!ZoomRectangle(view).IsEmpty)
                    throw new InvalidOperationException("No preview frame must mean no image coordinates or stale geometry.");
                checks.Add("Zoom, Fit, and pan controls disable when the preview disappears, with no stale image coordinates.");
                Results.Add(new { View = "camera-zoom-synthetic", width, height, Checks = checks.Count, Passed = true });
                allChecks.Add(new { width, height, Checks = checks, Passed = true });
            }
            finally
            {
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                root.Child = null;
                view.DataContext = null;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(Output, "camera-zoom-interactions.json"), JsonSerializer.Serialize(new
        {
            Fixture = "Detached WPF views with synthetic images and references. No camera, network, OS input, or model inference.",
            Cases = allChecks
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Camera preview zoom: mapping, anchor, pan clamps, overlays, frame retention, reference isolation, keyboard precision, and no-frame controls passed in two sizes.");
    }

    private static object? ZoomCall(CameraView view, string method, params object[] arguments) =>
        (typeof(CameraView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing camera zoom method: {method}."))
        .Invoke(view, arguments);

    private static T ZoomField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing camera fixture field: {name}."))
        .GetValue(instance)!;

    private static void ZoomField<T>(object instance, string name, T value) =>
        (instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing camera fixture field: {name}."))
        .SetValue(instance, value);

    private static Rect ZoomRectangle(CameraView view) => (Rect)(ZoomCall(view, "ImageRectangle")
        ?? throw new InvalidOperationException("The zoomed camera image rectangle was unavailable."));

    private static NormalizedPoint? ZoomMap(CameraView view, Point point, bool clamp) =>
        (NormalizedPoint?)ZoomCall(view, "PointInImage", point, clamp);

    private static void ZoomNear(double actual, double expected, string message, double tolerance = .01)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected) || Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{message} Expected {expected}, found {actual}.");
    }

    private static void ZoomPoint(NormalizedPoint? actual, NormalizedPoint expected, string message)
    {
        if (actual is not { } point) throw new InvalidOperationException(message + " Mapping returned no point.");
        ZoomNear(point.X, expected.X, message, 1e-8);
        ZoomNear(point.Y, expected.Y, message, 1e-8);
    }

    private static void ZoomRect(Rect actual, Rect expected, string message)
    {
        if (actual.IsEmpty || expected.IsEmpty) throw new InvalidOperationException(message + " Image rectangle is empty.");
        ZoomNear(actual.Left, expected.Left, message); ZoomNear(actual.Top, expected.Top, message);
        ZoomNear(actual.Width, expected.Width, message); ZoomNear(actual.Height, expected.Height, message);
    }

    private static void VerifyZoomOverlay(CameraView view, CameraViewModel camera, Rect rectangle)
    {
        var handles = ((Canvas)view.FindName("CornerOverlay")).Children.OfType<Border>().ToArray();
        if (handles.Length != camera.SelectedCorners.Count)
            throw new InvalidOperationException("Zooming must preserve every editable corner handle.");
        for (var index = 0; index < handles.Length; index++)
        {
            ZoomNear(handles[index].Width, 30, "Corner handles must remain a fixed 30 preview pixels wide.");
            ZoomNear(handles[index].Height, 30, "Corner handles must remain a fixed 30 preview pixels high.");
            ZoomNear(Canvas.GetLeft(handles[index]) + 15, rectangle.Left + camera.SelectedCorners[index].X * rectangle.Width,
                "Corner handle horizontal position must follow the displayed image.");
            ZoomNear(Canvas.GetTop(handles[index]) + 15, rectangle.Top + camera.SelectedCorners[index].Y * rectangle.Height,
                "Corner handle vertical position must follow the displayed image.");
        }
        var overlay = (Canvas)view.FindName("DetectionOverlay");
        var outlines = overlay.Children.OfType<Polygon>().Where(shape => shape.Stroke == Brushes.White).ToArray();
        if (overlay.IsHitTestVisible || outlines.Length != 2)
            throw new InvalidOperationException("Zoomed piece overlays must stay visible and must not intercept editing input.");
        var candidate = camera.PieceOutlines[0];
        for (var index = 0; index < 4; index++)
        {
            ZoomNear(outlines[0].Points[index].X, rectangle.Left + candidate.SensorOutline[index].X * rectangle.Width,
                "Train outline horizontal position must follow the displayed image.");
            ZoomNear(outlines[0].Points[index].Y, rectangle.Top + candidate.SensorOutline[index].Y * rectangle.Height,
                "Train outline vertical position must follow the displayed image.");
        }
        var marker = outlines[1].Points;
        ZoomNear(marker.Max(point => point.X) - marker.Min(point => point.X), marker.Max(point => point.Y) - marker.Min(point => point.Y),
            "Player-marker outline must remain square under preview zoom.");
    }

    private static void VerifyActualZoomedImage(CameraView view, Rect expected)
    {
        var image = (Image)view.FindName("PreviewImage");
        var area = (Grid)view.FindName("PreviewArea");
        var source = image.Source ?? throw new InvalidOperationException("Zoom fixture needs a displayed image source.");
        var scale = Math.Min(image.ActualWidth / source.Width, image.ActualHeight / source.Height);
        var content = new Rect((image.ActualWidth - source.Width * scale) / 2,
            (image.ActualHeight - source.Height * scale) / 2, source.Width * scale, source.Height * scale);
        var rendered = image.TransformToAncestor(area).TransformBounds(content);
        ZoomRect(rendered, expected, "The actual WPF image rendering must align with the coordinate mapper and overlays.");
    }
}
