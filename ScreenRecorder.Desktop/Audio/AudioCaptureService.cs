using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Desktop.Models;

namespace ScreenRecorder.Desktop.Audio;

public sealed class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? systemCapture;
    private WasapiCapture? microphoneCapture;
    private ConcurrentQueue<byte[]>? systemQueue;
    private ConcurrentQueue<byte[]>? microphoneQueue;
    private CancellationTokenSource? mixerCts;
    private Task? mixerTask;
    private Stopwatch? clock;

    public event Action<byte[], int, long>? AudioChunk;
    public event Action<string>? Status;

    public WaveFormat? OutputFormat { get; private set; }

    public void Start(AudioSourceMode mode, Stopwatch sharedClock)
    {
        clock = sharedClock;
        if (mode == AudioSourceMode.None)
        {
            OutputFormat = null;
            return;
        }

        switch (mode)
        {
            case AudioSourceMode.System:
                StartSystemOnly();
                break;
            case AudioSourceMode.Microphone:
                StartMicrophoneOnly();
                break;
            case AudioSourceMode.Both:
                StartMixed();
                break;
        }
    }

    private void StartSystemOnly()
    {
        systemCapture = new WasapiLoopbackCapture();
        OutputFormat = systemCapture.WaveFormat;
        systemCapture.DataAvailable += (_, e) => ForwardRaw(e.Buffer, e.BytesRecorded);
        systemCapture.StartRecording();
    }

    private void StartMicrophoneOnly()
    {
        var micDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        microphoneCapture = new WasapiCapture(micDevice);
        OutputFormat = microphoneCapture.WaveFormat;
        microphoneCapture.DataAvailable += (_, e) => ForwardRaw(e.Buffer, e.BytesRecorded);
        microphoneCapture.StartRecording();
    }

    private void StartMixed()
    {
        systemCapture = new WasapiLoopbackCapture();
        var micDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        microphoneCapture = new WasapiCapture(micDevice);

        if (!WaveFormatEquals(systemCapture.WaveFormat, microphoneCapture.WaveFormat))
        {
            Status?.Invoke("Microphone format differs from system loopback; falling back to system audio only.");
            OutputFormat = systemCapture.WaveFormat;
            systemCapture.DataAvailable += (_, e) => ForwardRaw(e.Buffer, e.BytesRecorded);
            systemCapture.StartRecording();
            return;
        }

        if (systemCapture.WaveFormat.BitsPerSample != 16)
        {
            Status?.Invoke("Mixed mode requires 16-bit PCM; falling back to system audio only.");
            OutputFormat = systemCapture.WaveFormat;
            systemCapture.DataAvailable += (_, e) => ForwardRaw(e.Buffer, e.BytesRecorded);
            systemCapture.StartRecording();
            return;
        }

        OutputFormat = systemCapture.WaveFormat;
        systemQueue = new ConcurrentQueue<byte[]>();
        microphoneQueue = new ConcurrentQueue<byte[]>();
        mixerCts = new CancellationTokenSource();

        systemCapture.DataAvailable += (_, e) => Enqueue(systemQueue, e.Buffer, e.BytesRecorded);
        microphoneCapture.DataAvailable += (_, e) => Enqueue(microphoneQueue, e.Buffer, e.BytesRecorded);
        systemCapture.StartRecording();
        microphoneCapture.StartRecording();

        mixerTask = Task.Run(() => MixLoop(mixerCts.Token), mixerCts.Token);
    }

    private void MixLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (systemQueue is null || microphoneQueue is null)
            {
                return;
            }

            var hasSystem = systemQueue.TryDequeue(out var systemBuffer);
            var hasMic = microphoneQueue.TryDequeue(out var microphoneBuffer);

            if (!hasSystem && !hasMic)
            {
                Thread.Sleep(4);
                continue;
            }

            if (hasSystem && !hasMic && systemBuffer is not null)
            {
                RaiseAudioChunk(systemBuffer, systemBuffer.Length);
                continue;
            }

            if (hasMic && !hasSystem && microphoneBuffer is not null)
            {
                RaiseAudioChunk(microphoneBuffer, microphoneBuffer.Length);
                continue;
            }

            if (systemBuffer is null || microphoneBuffer is null)
            {
                continue;
            }

            var mixed = MixPcm16(systemBuffer, microphoneBuffer);
            RaiseAudioChunk(mixed, mixed.Length);
        }
    }

    private static byte[] MixPcm16(byte[] a, byte[] b)
    {
        var length = Math.Max(a.Length, b.Length);
        if ((length & 1) == 1)
        {
            length--;
        }

        var output = new byte[length];
        for (var i = 0; i < length; i += 2)
        {
            var aSample = i + 1 < a.Length ? BitConverter.ToInt16(a, i) : (short)0;
            var bSample = i + 1 < b.Length ? BitConverter.ToInt16(b, i) : (short)0;
            var mixed = (short)Math.Clamp((aSample + bSample) / 2, short.MinValue, short.MaxValue);
            var bytes = BitConverter.GetBytes(mixed);
            output[i] = bytes[0];
            output[i + 1] = bytes[1];
        }

        return output;
    }

    private static bool WaveFormatEquals(WaveFormat a, WaveFormat b)
    {
        return a.Encoding == b.Encoding
               && a.SampleRate == b.SampleRate
               && a.BitsPerSample == b.BitsPerSample
               && a.Channels == b.Channels;
    }

    private static void Enqueue(ConcurrentQueue<byte[]> queue, byte[] source, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return;
        }

        var copy = new byte[bytesRecorded];
        Buffer.BlockCopy(source, 0, copy, 0, bytesRecorded);
        queue.Enqueue(copy);
    }

    private void ForwardRaw(byte[] source, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return;
        }

        var copy = new byte[bytesRecorded];
        Buffer.BlockCopy(source, 0, copy, 0, bytesRecorded);
        RaiseAudioChunk(copy, copy.Length);
    }

    private void RaiseAudioChunk(byte[] buffer, int count)
    {
        var timestampMs = clock is null ? 0L : (long)clock.Elapsed.TotalMilliseconds;
        AudioChunk?.Invoke(buffer, count, timestampMs);
    }

    public async Task StopAsync()
    {
        mixerCts?.Cancel();

        if (systemCapture is not null)
        {
            systemCapture.StopRecording();
        }

        if (microphoneCapture is not null)
        {
            microphoneCapture.StopRecording();
        }

        if (mixerTask is not null)
        {
            try
            {
                await mixerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation.
            }
        }
    }

    public void Dispose()
    {
        mixerCts?.Dispose();
        mixerTask?.Dispose();
        systemCapture?.Dispose();
        microphoneCapture?.Dispose();
    }
}
