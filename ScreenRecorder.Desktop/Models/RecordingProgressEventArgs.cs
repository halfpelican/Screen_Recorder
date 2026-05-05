namespace ScreenRecorder.Desktop.Models;

public sealed class RecordingProgressEventArgs : EventArgs
{
    public RecordingProgressEventArgs(long frameCount, TimeSpan elapsed)
    {
        FrameCount = frameCount;
        Elapsed = elapsed;
    }

    public long FrameCount { get; }
    public TimeSpan Elapsed { get; }
}
