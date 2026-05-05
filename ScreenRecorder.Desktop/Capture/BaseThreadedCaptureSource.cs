using System.Diagnostics;
using System.Drawing;
using System.Windows;
using ScreenRecorder.Desktop.Models;

namespace ScreenRecorder.Desktop.Capture;

public abstract class BaseThreadedCaptureSource : IScreenCaptureSource
{
    public abstract string Name { get; }
    public abstract bool IsAvailable { get; }

    public virtual (int Width, int Height) GetFrameSize(CaptureRegion? region)
    {
        var area = ResolveCaptureArea(region);
        return (area.Width, area.Height);
    }

    public virtual void RunCaptureLoop(
        CaptureRegion? region,
        int fps,
        Stopwatch clock,
        Action<Bitmap, long> onFrame,
        CancellationToken cancellationToken)
    {
        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be greater than zero.");
        }

        var area = ResolveCaptureArea(region);
        var frameInterval = TimeSpan.FromSeconds(1d / fps);
        var nextFrameAt = clock.Elapsed;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = clock.Elapsed;
            if (now < nextFrameAt)
            {
                var delay = nextFrameAt - now;
                if (delay.TotalMilliseconds > 1)
                {
                    Thread.Sleep(delay);
                }

                continue;
            }

            var frame = CaptureFrame(area);
            var timestampMs = (long)clock.Elapsed.TotalMilliseconds;
            onFrame(frame, timestampMs);
            nextFrameAt += frameInterval;
        }
    }

    protected static Rectangle ResolveCaptureArea(CaptureRegion? region)
    {
        if (region is { IsValid: true })
        {
            return new Rectangle(region.Left, region.Top, region.Width, region.Height);
        }

        var left = (int)SystemParameters.VirtualScreenLeft;
        var top = (int)SystemParameters.VirtualScreenTop;
        var width = (int)SystemParameters.VirtualScreenWidth;
        var height = (int)SystemParameters.VirtualScreenHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("No primary display was found.");
        }

        return new Rectangle(left, top, width, height);
    }

    protected virtual Bitmap CaptureFrame(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        return bitmap;
    }

    public virtual void Dispose()
    {
    }
}
