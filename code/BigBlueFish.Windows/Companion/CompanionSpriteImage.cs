using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BigBlueFish.Windows;
internal sealed class CompanionSpriteImage : Image
{
    internal const byte AlphaThreshold = 8;
    private static readonly Cursor palm = LoadCursor("Palm");
    private static readonly Cursor palmGrab = LoadCursor("PalmGrab");
    private byte[]? framePixels;
    private int frameWidth, frameHeight;

    internal CompanionSpriteImage() => Cursor = palm;

    internal static Cursor PalmGrab => palmGrab;
    internal static string? LoadError { get; private set; }

    /// <summary>Open palm while hovering, closed palm while the button is down.</summary>
    internal void SetPressed(bool pressed)
    {
        var next = pressed ? palmGrab : palm;
        if (!ReferenceEquals(Cursor, next)) Cursor = next;
        // WPF only re-evaluates the pointer shape on the next mouse move; without this the
        // grip would not show until the user moved while holding the button.
        Mouse.UpdateCursor();
    }

    // The cursors ship as resources, so a missing one is a packaging bug rather than a user
    // condition; --cursor verification fails if this falls back. Falling back to the system
    // hand keeps the pet usable if a trimmed build drops the resource.
    private static Cursor LoadCursor(string name)
    {
        byte[] bytes;
        try
        {
            var part = Application.GetResourceStream(new Uri($"/BigBlueFish;component/Res/Cursor/{name}.cur", UriKind.Relative));
            if (part == null)
            {
                LoadError = name + ": 资源不存在";
                return Cursors.Hand;
            }
            using var stream = part.Stream;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception error)
        {
            LoadError = name + ": " + error.GetType().Name + " " + error.Message;
            return Cursors.Hand;
        }

        // WPF's stream-based Cursor stages the image under %TEMP%\WPF, which some locked-down
        // sandboxes refuse. Loading from a file in our own directory avoids that temp copy.
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Cursor(stream);
        }
        catch (Exception error)
        {
            LoadError = name + ": " + error.GetType().Name + " " + error.Message;
        }

        try
        {
            var folder = Path.Combine(Environment.CurrentDirectory, "cursors");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name + ".cur");
            File.WriteAllBytes(path, bytes);
            LoadError = null;
            return new Cursor(path);
        }
        catch (Exception error)
        {
            LoadError = name + ": " + error.GetType().Name + " " + error.Message;
        }
        return Cursors.Hand;
    }

    internal void UpdatePixels(byte[]? pixels, int width, int height)
    {
        frameWidth = width;
        frameHeight = height;
        framePixels = pixels;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var pixels = framePixels;
        int w = frameWidth, h = frameHeight;
        if (pixels != null && w > 0 && h > 0 && pixels.Length >= w * h * 4)
        {
            int px = (int)(parameters.HitPoint.X * w / ActualWidth);
            int py = (int)(parameters.HitPoint.Y * h / ActualHeight);
            return px >= 0 && py >= 0 && px < w && py < h && pixels[(py * w + px) * 4 + 3] >= AlphaThreshold
                ? new PointHitTestResult(this, parameters.HitPoint) : null;
        }
        if (Source is not BitmapSource bitmap) return null;
        int x = (int)(parameters.HitPoint.X * bitmap.PixelWidth / ActualWidth);
        int y = (int)(parameters.HitPoint.Y * bitmap.PixelHeight / ActualHeight);
        if (x < 0 || y < 0 || x >= bitmap.PixelWidth || y >= bitmap.PixelHeight) return null;
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3] >= AlphaThreshold ? new PointHitTestResult(this, parameters.HitPoint) : null;
    }
}
