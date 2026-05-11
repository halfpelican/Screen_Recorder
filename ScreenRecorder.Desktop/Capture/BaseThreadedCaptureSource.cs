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
    /// Uses a thread-safe retrieval for virtual screen bounds.
    /// </summary>
    protected static Rectangle ResolveCaptureArea(CaptureRegion? region)
    {
        if (region is { IsValid: true })
        {
            return new Rectangle(region.Left, region.Top, region.Width, region.Height);
        }

        // Try to get virtual screen bounds in a thread-safe way
        var bounds = TryGetVirtualScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("No primary display was found.");
        }
        return bounds;
    }

    /// <summary>
    /// Safely retrieves the virtual screen bounds, marshaling to the dispatcher if needed.
    /// </summary>
    private static Rectangle TryGetVirtualScreenBounds()
    {
#if WINDOWS
        // If called from a non-UI thread, SystemParameters is not safe. Use GetSystemMetrics.
        try
        {
            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == true)
            {
                // UI thread: safe to use SystemParameters
                return new Rectangle(
                    (int)System.Windows.SystemParameters.VirtualScreenLeft,
                    (int)System.Windows.SystemParameters.VirtualScreenTop,
                    (int)System.Windows.SystemParameters.VirtualScreenWidth,
                    (int)System.Windows.SystemParameters.VirtualScreenHeight);
            }
            else if (System.Windows.Application.Current?.Dispatcher != null)
            {
                // Not UI thread, but dispatcher available: marshal synchronously
                return System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    new Rectangle(
                        (int)System.Windows.SystemParameters.VirtualScreenLeft,
                        (int)System.Windows.SystemParameters.VirtualScreenTop,
                        (int)System.Windows.SystemParameters.VirtualScreenWidth,
                        (int)System.Windows.SystemParameters.VirtualScreenHeight));
            }
        }
        catch
        {
            // Fallback to GetSystemMetrics below
        }
#endif
        // Fallback: use GetSystemMetrics (thread-safe, but may not reflect DPI scaling)
        var left   = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top    = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
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
