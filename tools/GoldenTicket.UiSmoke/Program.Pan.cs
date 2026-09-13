using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static async Task VerifyPreviewPanGestures()
    {
        BindingLog.Context = "camera-pan-gestures";
        await using var camera = new CameraViewModel
        {
            Preview = SyntheticCropFixture(), IsRunning = true, SelectingCorners = true
        };
        var view = new CameraView { DataContext = camera };
        var checks = new List<string>();
        try
        {
            await Arrange(view, 1280, 800);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await Arrange(view, 1280, 800);
            var area = (Grid)view.FindName("PreviewArea");
            var image = (Image)view.FindName("PreviewImage");
            var center = new Point(area.ActualWidth / 2, area.ActualHeight / 2);
            var source = new FixturePresentationSource { RootVisual = view };
            bool Call(string name, params object[] arguments) => (bool)(ZoomCall(view, name, arguments) ?? false);
            bool Begin(Point point) => Call("BeginBackgroundPress", point);
            bool Move(Point point) => Call("UpdatePanGesture", point, MouseButtonState.Pressed, MouseButtonState.Released);
            bool Release(Point point) => Call("CompletePanGesture", point, MouseButton.Left);
            void Press(Key key) => image.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.KeyDownEvent });
            void NoCorners(string message)
            {
                if (camera.SelectedCorners.Count != 0) throw new InvalidOperationException(message);
            }
            void Prepare()
            {
                ZoomCall(view, "EndPan");
                camera.IsBusy = false;
                camera.Preview ??= SyntheticCropFixture();
                camera.SelectedCorners.Clear();
                camera.SelectingCorners = true;
                ZoomCall(view, "ResetZoom");
                ZoomCall(view, "SetZoom", 3d, center);
            }

            if (Begin(center)) throw new InvalidOperationException("Fit must retain its existing corner-placement behavior.");
            Prepare();
            var startRectangle = ZoomRectangle(view);
            if (!Begin(center)) throw new InvalidOperationException("A zoomed background press must start without Pan or a modifier.");
            NoCorners("Pressing the zoomed background must not place a corner before click versus drag is known.");
            var delta = new Vector(SystemParameters.MinimumHorizontalDragDistance + 25,
                SystemParameters.MinimumVerticalDragDistance + 15);
            if (!Move(center + delta) || !Release(center + delta))
                throw new InvalidOperationException("An ordinary background drag must be handled through release.");
            await Arrange(view, 1280, 800);
            var moved = ZoomRectangle(view);
            ZoomNear(moved.Left, startRectangle.Left + delta.X, "Background drag must move the viewport horizontally.");
            ZoomNear(moved.Top, startRectangle.Top + delta.Y, "Background drag must move the viewport vertically.");
            VerifyActualZoomedImage(view, moved);
            NoCorners("Panning during corner selection must not place any corner.");
            checks.Add("At 3×, unmodified background dragging pans the rendered image during corner selection without placing a corner; Fit rejects this gesture.");

            Prepare();
            var normalizedClick = ZoomMap(view, center, false)!.Value;
            var small = new Vector(SystemParameters.MinimumHorizontalDragDistance / 2,
                SystemParameters.MinimumVerticalDragDistance / 2);
            Begin(center);
            Move(center + small);
            NoCorners("Movement below the system drag threshold must defer corner placement until release.");
            if (!Release(center + small) || camera.SelectedCorners.Count != 1)
                throw new InvalidOperationException("A zoomed click must place exactly one corner when released.");
            ZoomPoint(camera.SelectedCorners[0], normalizedClick, "The delayed click must retain its normalized source coordinate.");
            if (Release(center + small) || camera.SelectedCorners.Count != 1)
                throw new InvalidOperationException("A second release must not repeat a completed click.");
            if (Begin(center)) throw new InvalidOperationException("A numbered corner handle must take priority over background pan.");
            checks.Add("A small movement is a click: one corner appears only on release at the source coordinate; its handle takes priority over pan.");

            Prepare();
            var beforeRoundTrip = ZoomRectangle(view);
            Begin(center); Move(center + delta); Move(center); Release(center);
            NoCorners("Dragging away and back must remain a pan rather than becoming a click on release.");
            ZoomRect(ZoomRectangle(view), beforeRoundTrip, "A round-trip drag must restore the starting view without placing a corner.");
            Prepare();
            var beforeReleaseOnly = ZoomRectangle(view);
            Begin(center); Release(center + delta);
            NoCorners("A displaced release without a mouse-move event must not place a corner.");
            ZoomNear(ZoomRectangle(view).Left, beforeReleaseOnly.Left + delta.X,
                "A displaced release must apply the pan even without an intermediate mouse-move event.");
            checks.Add("Drag out-and-back remains a pan, and a displaced release without a mouse-move event also pans instead of placing a corner.");

            var cancellations = new (string Name, Action Cancel)[]
            {
                ("Escape", () => Press(Key.Escape)),
                ("capture loss", () => ZoomCall(view, "PreviewImage_LostMouseCapture", area, null!)),
                ("focus loss", () => ZoomCall(view, "PreviewImage_LostKeyboardFocus", image, null!)),
                ("crop reset", () => camera.SelectedCorners.Clear()),
                ("selection change", () => camera.SelectingCorners = false),
                ("busy transition", () => camera.IsBusy = true),
                ("Fit", () => ZoomCall(view, "ResetZoom")),
                ("preview loss", () => camera.Preview = null)
            };
            foreach (var (name, cancel) in cancellations)
            {
                Prepare();
                if (!Begin(center)) throw new InvalidOperationException($"The {name} cancellation fixture could not begin a click.");
                cancel();
                if (Release(center)) throw new InvalidOperationException($"{name} must cancel the pending click before release.");
                NoCorners($"{name} must not leave a delayed corner placement behind.");
            }
            checks.Add("Escape, capture/focus loss, crop/selection changes, busy state, Fit, and missing preview cancel a pending click without placing a corner.");

            Prepare();
            Begin(center); Move(center + delta);
            image.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyUpEvent });
            if (!ZoomField<bool>(view, "_panning"))
                throw new InvalidOperationException("An unrelated Space release must not cancel an ordinary mouse pan.");
            Release(center + delta);
            NoCorners("Releasing Space during an ordinary mouse pan must never place a corner.");
            checks.Add("An unrelated Space key-up preserves an ordinary mouse drag.");

            Prepare();
            camera.SelectingCorners = false;
            NormalizedPoint[] corners = [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];
            foreach (var corner in corners) camera.SelectedCorners.Add(corner);
            var pixels = new byte[camera.Preview!.PixelWidth * camera.Preview.PixelHeight * 4];
            camera.Preview.CopyPixels(pixels, camera.Preview.PixelWidth * 4, 0);
            var frame = CameraFrame.CopyFromBgra32(camera.Preview.PixelWidth, camera.Preview.PixelHeight, pixels);
            var registration = BoardRegistration.Create(frame, corners);
            ZoomField<BoardRegistration?>(camera, "_registration", registration);
            camera.HasBoardCrop = true;
            var monitor = ZoomField<SceneReferenceMonitor>(camera, "_monitor");
            monitor.SetReference(frame);
            var detector = ZoomField<PieceCandidateDetector>(camera, "_pieceDetector");
            detector.SetReference(frame);
            camera.HasPieceReference = true;
            var cropRevision = ZoomField<long>(camera, "_cropRevision");
            var referenceRevision = ZoomField<long>(camera, "_referenceRevision");
            var detectorRevision = detector.ReferenceRevision;
            var sceneRevision = monitor.Current.EvidenceRevision;
            if (!Begin(center) || !Move(center + delta) || !Release(center + delta))
                throw new InvalidOperationException("An already cropped board must also accept ordinary background dragging.");
            if (!camera.SelectedCorners.SequenceEqual(corners) || !camera.HasBoardCrop || !camera.HasPieceReference ||
                !ReferenceEquals(ZoomField<BoardRegistration?>(camera, "_registration"), registration) ||
                ZoomField<long>(camera, "_cropRevision") != cropRevision ||
                ZoomField<long>(camera, "_referenceRevision") != referenceRevision ||
                !detector.HasReference || detector.ReferenceRevision != detectorRevision ||
                !monitor.HasReference || monitor.Current.EvidenceRevision != sceneRevision)
                throw new InvalidOperationException("Ordinary background dragging must preserve the saved crop and both references.");
            checks.Add("Dragging a completed crop preserves all normalized corners, registration, crop revisions, and synthetic scene/piece references.");
            Results.Add(new { View = "camera-pan-gestures-synthetic", width = 1280, height = 800, Checks = checks.Count, Passed = true });
            await File.WriteAllTextAsync(Path.Combine(Output, "camera-pan-interactions.json"), JsonSerializer.Serialize(new
            {
                Fixture = "Detached WPF view and production gesture helpers with synthetic coordinates/images. Native pointer delivery, capture, and OS input are not exercised.",
                Checks = checks, Passed = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Camera preview drag: automatic pan, deferred clicks, handle priority, threshold/release edge cases, cancellation, and crop/reference preservation passed.");
        }
        finally
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            view.DataContext = null;
        }
    }
}
