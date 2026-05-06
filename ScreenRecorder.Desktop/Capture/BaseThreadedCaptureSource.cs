using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ScreenRecorder.Desktop.Models;

namespace ScreenRecorder.Desktop.Capture;

public abstract class BaseThreadedCaptureSource : IScreenCaptureSource
{
    // GetSystemMetrics indices for the virtual screen — safe to call from any thread.
    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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

    /// <summary>
    /// Resolves the capture area from either the supplied region or the full virtual screen.
    /// Uses <c>GetSystemMetrics</c> (thread-safe) instead of <c>SystemParameters</c>
    /// (which requires the WPF dispatcher and crashes when called from a background thread).
    /// </summary>
    protected static Rectangle ResolveCaptureArea(CaptureRegion? region)
    {
        if (region is { IsValid: true })
        {
            return new Rectangle(region.Left, region.Top, region.Width, region.Height);
        }

        var left   = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top    = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

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
