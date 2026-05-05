using System.Diagnostics;
using System.Drawing;
using ScreenRecorder.Desktop.Models;

namespace ScreenRecorder.Desktop.Capture;

public interface IScreenCaptureSource : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    (int Width, int Height) GetFrameSize(CaptureRegion? region);

    void RunCaptureLoop(
        CaptureRegion? region,
        int fps,
        Stopwatch clock,
        Action<Bitmap, long> onFrame,
        CancellationToken cancellationToken);
}
