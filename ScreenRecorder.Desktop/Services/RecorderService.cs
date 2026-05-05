using System.Diagnostics;
using System.Drawing;
using ScreenRecorder.Desktop.Audio;
using ScreenRecorder.Desktop.Capture;
using ScreenRecorder.Desktop.Encoding;
using ScreenRecorder.Desktop.Models;

namespace ScreenRecorder.Desktop.Services;

public sealed class RecorderService : IDisposable
{
    private readonly object sinkGate = new();
    private RecorderOptions? activeOptions;
    private CancellationTokenSource? cts;
    private Task? captureTask;
    private Stopwatch? clock;
    private IScreenCaptureSource? captureSource;
    private AudioCaptureService? audioCaptureService;
    private IRecordingSink? recordingSink;
    private long frameCount;
    private bool stopping;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public bool IsRecording { get; private set; }

    public Task StartAsync(RecorderOptions options)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("A recording is already in progress.");
        }

        ValidateOptions(options);
        activeOptions = options;
        frameCount = 0;
        stopping = false;

        cts = new CancellationTokenSource();
        clock = Stopwatch.StartNew();
        captureSource = CreateCaptureSource();
        var frameSize = ResolveFrameSizeWithFallback(options.Region);
        audioCaptureService = new AudioCaptureService();
        audioCaptureService.Status += message => OnStatus(message);
        audioCaptureService.AudioChunk += HandleAudioChunk;

        recordingSink = CreateRecordingSink(options, out var fallbackMessage);
        if (!string.IsNullOrWhiteSpace(fallbackMessage))
        {
            OnStatus(fallbackMessage);
        }

        if (options.AudioMode != AudioSourceMode.None)
        {
            audioCaptureService.Start(options.AudioMode, clock);
        }

        recordingSink.Initialize(
            options.OutputPath,
            frameSize.Width,
            frameSize.Height,
            options.Fps,
            audioCaptureService.OutputFormat);

        captureTask = Task.Run(
            () => captureSource.RunCaptureLoop(
                options.Region,
                options.Fps,
                clock,
                HandleVideoFrame,
                cts.Token),
            cts.Token);

        IsRecording = true;
        OnStatus($"Recording via {captureSource.Name} + {recordingSink.Name}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRecording || stopping)
        {
            return;
        }

        stopping = true;
        cts?.Cancel();

        if (captureTask is not null)
        {
            try
            {
                await captureTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation.
            }
        }

        if (audioCaptureService is not null)
        {
            await audioCaptureService.StopAsync().ConfigureAwait(false);
        }

        lock (sinkGate)
        {
            recordingSink?.Complete();
        }

        var elapsed = clock?.Elapsed ?? TimeSpan.Zero;
        var outputPath = recordingSink?.OutputPath ?? activeOptions?.OutputPath ?? string.Empty;
        var captureBackend = captureSource?.Name ?? "Unknown";
        var encoderBackend = recordingSink?.Name ?? "Unknown";

        IsRecording = false;
        OnStatus("Saved");
        RecordingCompleted?.Invoke(
            this,
            new RecordingCompletedEventArgs(
                outputPath,
                frameCount,
                elapsed,
                captureBackend,
                encoderBackend));

        DisposeSession();
    }

    private void HandleVideoFrame(Bitmap bitmap, long timestampMs)
    {
        try
        {
            lock (sinkGate)
            {
                recordingSink?.WriteVideoFrame(bitmap, timestampMs);
                frameCount++;
                if (frameCount % 10 == 0 && clock is not null)
                {
                    ProgressChanged?.Invoke(this, new RecordingProgressEventArgs(frameCount, clock.Elapsed));
                }
            }
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private void HandleAudioChunk(byte[] data, int count, long timestampMs)
    {
        lock (sinkGate)
        {
            recordingSink?.WriteAudioChunk(data, count, timestampMs);
        }
    }

    private static void ValidateOptions(RecorderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new ArgumentException("Output path is required.", nameof(options.OutputPath));
        }

        if (options.Fps is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Fps), "FPS must be between 1 and 120.");
        }
    }

    private static IScreenCaptureSource CreateCaptureSource()
    {
        var wgc = new WindowsGraphicsCaptureSource();
        if (wgc.IsAvailable)
        {
            return wgc;
        }

        return new DxgiDesktopDuplicationSource();
    }

    private (int Width, int Height) ResolveFrameSizeWithFallback(CaptureRegion? region)
    {
        if (captureSource is null)
        {
            throw new InvalidOperationException("Capture source was not initialized.");
        }

        try
        {
            return captureSource.GetFrameSize(region);
        }
        catch (Exception ex) when (captureSource is WindowsGraphicsCaptureSource)
        {
            captureSource.Dispose();
            captureSource = new DxgiDesktopDuplicationSource();
            OnStatus($"Windows.Graphics.Capture unavailable at runtime ({ex.Message}). Falling back to {captureSource.Name}.");
            return captureSource.GetFrameSize(region);
        }
    }

    private static IRecordingSink CreateRecordingSink(RecorderOptions options, out string fallbackMessage)
    {
        if (MediaFoundationMp4Sink.TryCreate(out var mediaFoundationSink, out var reason) && mediaFoundationSink is not null)
        {
            fallbackMessage = string.Empty;
            return mediaFoundationSink;
        }

        fallbackMessage = $"Media Foundation unavailable ({reason}). Falling back to SharpAvi MJPEG AVI.";
        return new SharpAviRecordingSink();
    }

    private void OnStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    private void DisposeSession()
    {
        cts?.Dispose();
        cts = null;

        captureTask?.Dispose();
        captureTask = null;

        captureSource?.Dispose();
        captureSource = null;

        audioCaptureService?.Dispose();
        audioCaptureService = null;

        recordingSink?.Dispose();
        recordingSink = null;

        clock = null;
        activeOptions = null;
        stopping = false;
    }

    public void Dispose()
    {
        DisposeSession();
    }
}
