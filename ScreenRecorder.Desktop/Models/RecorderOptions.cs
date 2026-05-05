namespace ScreenRecorder.Desktop.Models;

public sealed class RecorderOptions
{
    public string OutputPath { get; set; } = string.Empty;
    public int Fps { get; set; } = 20;
    public CaptureRegion? Region { get; set; }
    public AudioSourceMode AudioMode { get; set; } = AudioSourceMode.System;
}
