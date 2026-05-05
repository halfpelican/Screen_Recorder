namespace ScreenRecorder.Desktop.Models;

public sealed class RecordingCompletedEventArgs : EventArgs
{
    public RecordingCompletedEventArgs(
        string outputPath,
        long frameCount,
        TimeSpan elapsed,
        string captureBackend,
        string encodingBackend)
    {
        OutputPath = outputPath;
        FrameCount = frameCount;
        Elapsed = elapsed;
        CaptureBackend = captureBackend;
        EncodingBackend = encodingBackend;
    }

    public string OutputPath { get; }
    public long FrameCount { get; }
    public TimeSpan Elapsed { get; }
    public string CaptureBackend { get; }
    public string EncodingBackend { get; }
    public double AverageFps => Elapsed.TotalSeconds <= 0 ? 0 : FrameCount / Elapsed.TotalSeconds;
}
