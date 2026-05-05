using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.Wave;
using ScreenRecorder.Desktop.Encoding.Interop;

namespace ScreenRecorder.Desktop.Encoding;

public sealed class MediaFoundationMp4Sink : IRecordingSink
{
    private const int MfVideoInterlaceProgressive = 2;
    private const int AacLcProfile = 0x29;
    private const long HnsPerSecond = 10_000_000;

    private readonly object _gate = new();
    private IMFSinkWriter? _sinkWriter;
    private bool _initialized;
    private bool _mfStarted;
    private int _videoStreamIndex;
    private int _audioStreamIndex = -1;
    private int _width;
    private int _height;
    private int _fps;
    private int _videoStride;
    private long _frameDuration;
    private int _audioBytesPerSecond;
    private byte[]? _videoBuffer;
    private byte[]? _videoRowBuffer;

    public string Name => "Media Foundation H.264 MP4";
    public string OutputPath { get; private set; } = string.Empty;

    private MediaFoundationMp4Sink() { }

    public static bool TryCreate(out MediaFoundationMp4Sink? sink, out string reason)
    {
        sink = null;
        reason = string.Empty;
        int hr = MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL);
        if (hr != 0)
        {
            reason = $"MFStartup failed: 0x{hr:X8}";
            return false;
        }

        sink = new MediaFoundationMp4Sink { _mfStarted = true };
        return true;
    }

    public void Initialize(string outputPath, int width, int height, int fps, WaveFormat? audioFormat)
    {
        lock (_gate)
        {
            if (_initialized)
            {
                throw new InvalidOperationException("Already initialized");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path is required.", nameof(outputPath));
            }

            OutputPath = outputPath;
            _width = width;
            _height = height;
            _fps = fps;
            _videoStride = checked(width * 4);
            _frameDuration = HnsPerSecond / Math.Max(1, fps);

            try
            {
                CheckHr(MF.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, null, out _sinkWriter), "MFCreateSinkWriterFromURL");
                ConfigureVideoStream();
                if (audioFormat is not null)
                {
                    ConfigureAudioStream(audioFormat);
                }

                CheckHr(_sinkWriter!.BeginWriting(), "IMFSinkWriter.BeginWriting");
                _initialized = true;
            }
            catch
            {
                ReleaseSinkWriter();
                throw;
            }
        }
    }

    public void WriteVideoFrame(Bitmap frame, long timestampMs)
    {
        lock (_gate)
        {
            EnsureInitialized();

            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            Bitmap? preparedFrame = null;
            BitmapData? bitmapData = null;

            try
            {
                preparedFrame = PrepareFrame(frame);
                var rect = new Rectangle(0, 0, _width, _height);
                bitmapData = preparedFrame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                var bufferSize = checked(_videoStride * _height);
                CheckHr(MF.MFCreateMemoryBuffer(bufferSize, out buffer), "MFCreateMemoryBuffer");
                CheckHr(buffer.Lock(out var destination, out _, out _), "IMFMediaBuffer.Lock");
                try
                {
                    CopyBitmapToBuffer(bitmapData, destination, bufferSize);
                }
                finally
                {
                    buffer.Unlock();
                }

                CheckHr(buffer.SetCurrentLength(bufferSize), "IMFMediaBuffer.SetCurrentLength");
                CheckHr(MF.MFCreateSample(out sample), "MFCreateSample");
                CheckHr(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
                CheckHr(sample.SetSampleTime(ToHns(timestampMs)), "IMFSample.SetSampleTime");
                CheckHr(sample.SetSampleDuration(_frameDuration), "IMFSample.SetSampleDuration");
                CheckHr(_sinkWriter!.WriteSample(_videoStreamIndex, sample), "IMFSinkWriter.WriteSample(video)");
            }
            finally
            {
                if (bitmapData is not null && preparedFrame is not null)
                {
                    preparedFrame.UnlockBits(bitmapData);
                }

                if (preparedFrame is not null && !ReferenceEquals(preparedFrame, frame))
                {
                    preparedFrame.Dispose();
                }

                ReleaseComObject(sample);
                ReleaseComObject(buffer);
            }
        }
    }

    public void WriteAudioChunk(byte[] data, int count, long timestampMs)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            EnsureInitialized();
            if (_audioStreamIndex < 0 || _audioBytesPerSecond <= 0)
            {
                return;
            }

            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;

            try
            {
                CheckHr(MF.MFCreateMemoryBuffer(count, out buffer), "MFCreateMemoryBuffer");
                CheckHr(buffer.Lock(out var destination, out _, out _), "IMFMediaBuffer.Lock");
                try
                {
                    Marshal.Copy(data, 0, destination, count);
                }
                finally
                {
                    buffer.Unlock();
                }

                CheckHr(buffer.SetCurrentLength(count), "IMFMediaBuffer.SetCurrentLength");
                CheckHr(MF.MFCreateSample(out sample), "MFCreateSample");
                CheckHr(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
                CheckHr(sample.SetSampleTime(ToHns(timestampMs)), "IMFSample.SetSampleTime");

                var duration = (long)Math.Round(count * (double)HnsPerSecond / _audioBytesPerSecond);
                if (duration > 0)
                {
                    CheckHr(sample.SetSampleDuration(duration), "IMFSample.SetSampleDuration");
                }

                CheckHr(_sinkWriter!.WriteSample(_audioStreamIndex, sample), "IMFSinkWriter.WriteSample(audio)");
            }
            finally
            {
                ReleaseComObject(sample);
                ReleaseComObject(buffer);
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                if (_sinkWriter is not null)
                {
                    CheckHr(_sinkWriter.Finalize(), "IMFSinkWriter.Finalize");
                }
            }
            finally
            {
                ReleaseSinkWriter();
                _initialized = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                Complete();
            }

            if (_mfStarted)
            {
                MF.MFShutdown();
                _mfStarted = false;
            }
        }
    }

    private void ConfigureVideoStream()
    {
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;

        try
        {
            CheckHr(MF.MFCreateMediaType(out outputType), "MFCreateMediaType(video output)");
            CheckHr(outputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video), "Set video major type");
            CheckHr(outputType.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_H264), "Set video subtype");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AVG_BITRATE, CalculateVideoBitrate()), "Set video bitrate");
            CheckHr(outputType.SetUINT32(MF.MF_MT_INTERLACE_MODE, MfVideoInterlaceProgressive), "Set interlace mode");
            CheckHr(MF.MFSetAttributeSize(outputType, MF.MF_MT_FRAME_SIZE, (uint)_width, (uint)_height), "Set frame size");
            CheckHr(MF.MFSetAttributeRatio(outputType, MF.MF_MT_FRAME_RATE, (uint)_fps, 1), "Set frame rate");
            CheckHr(MF.MFSetAttributeRatio(outputType, MF.MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Set pixel aspect ratio");
            CheckHr(_sinkWriter!.AddStream(outputType, out _videoStreamIndex), "IMFSinkWriter.AddStream(video)");

            CheckHr(MF.MFCreateMediaType(out inputType), "MFCreateMediaType(video input)");
            CheckHr(inputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video), "Set video input major");
            CheckHr(inputType.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32), "Set video input subtype");
            CheckHr(inputType.SetUINT32(MF.MF_MT_INTERLACE_MODE, MfVideoInterlaceProgressive), "Set video input interlace");
            CheckHr(MF.MFSetAttributeSize(inputType, MF.MF_MT_FRAME_SIZE, (uint)_width, (uint)_height), "Set input frame size");
            CheckHr(MF.MFSetAttributeRatio(inputType, MF.MF_MT_FRAME_RATE, (uint)_fps, 1), "Set input frame rate");
            CheckHr(MF.MFSetAttributeRatio(inputType, MF.MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Set input pixel aspect ratio");
            CheckHr(inputType.SetUINT32(MF.MF_MT_DEFAULT_STRIDE, _videoStride), "Set input stride");
            CheckHr(inputType.SetUINT32(MF.MF_MT_FIXED_SIZE_SAMPLES, 1), "Set fixed size samples");
            CheckHr(inputType.SetUINT32(MF.MF_MT_SAMPLE_SIZE, _videoStride * _height), "Set sample size");
            CheckHr(inputType.SetUINT32(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1), "Set samples independent");
            CheckHr(_sinkWriter!.SetInputMediaType(_videoStreamIndex, inputType, null), "Set input media type(video)");
        }
        finally
        {
            ReleaseComObject(inputType);
            ReleaseComObject(outputType);
        }
    }

    private void ConfigureAudioStream(WaveFormat audioFormat)
    {
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;

        try
        {
            _audioBytesPerSecond = audioFormat.AverageBytesPerSecond;

            CheckHr(MF.MFCreateMediaType(out outputType), "MFCreateMediaType(audio output)");
            CheckHr(outputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio), "Set audio major type");
            CheckHr(outputType.SetGUID(MF.MF_MT_SUBTYPE, MF.MFAudioFormat_AAC), "Set audio subtype");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, audioFormat.Channels), "Set audio channels");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, audioFormat.SampleRate), "Set audio sample rate");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "Set audio bits per sample");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1), "Set audio block alignment");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, CalculateAudioBitrate(audioFormat)), "Set audio bitrate");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, AacLcProfile), "Set AAC profile");
            CheckHr(_sinkWriter!.AddStream(outputType, out _audioStreamIndex), "IMFSinkWriter.AddStream(audio)");

            CheckHr(MF.MFCreateMediaType(out inputType), "MFCreateMediaType(audio input)");
            CheckHr(inputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio), "Set input audio major");
            CheckHr(inputType.SetGUID(MF.MF_MT_SUBTYPE, ResolveAudioSubtype(audioFormat)), "Set input audio subtype");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, audioFormat.Channels), "Set input audio channels");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, audioFormat.SampleRate), "Set input audio sample rate");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, audioFormat.BitsPerSample), "Set input bits per sample");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, audioFormat.BlockAlign), "Set input block alignment");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, audioFormat.AverageBytesPerSecond), "Set input avg bytes");
            CheckHr(_sinkWriter!.SetInputMediaType(_audioStreamIndex, inputType, null), "Set input media type(audio)");
        }
        finally
        {
            ReleaseComObject(inputType);
            ReleaseComObject(outputType);
        }
    }

    private Bitmap PrepareFrame(Bitmap frame)
    {
        if (frame.Width == _width && frame.Height == _height && frame.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return frame;
        }

        var converted = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(converted);
        graphics.DrawImage(frame, new Rectangle(0, 0, _width, _height));
        return converted;
    }

    private void CopyBitmapToBuffer(BitmapData data, IntPtr destination, int bufferSize)
    {
        var widthBytes = _videoStride;
        if (data.Stride == widthBytes)
        {
            EnsureVideoBuffer(bufferSize);
            Marshal.Copy(data.Scan0, _videoBuffer!, 0, bufferSize);
            Marshal.Copy(_videoBuffer!, 0, destination, bufferSize);
            return;
        }

        EnsureVideoRowBuffer(widthBytes);
        var sourceRow = data.Scan0;
        var sourceStep = data.Stride;

        for (var row = 0; row < _height; row++)
        {
            Marshal.Copy(sourceRow, _videoRowBuffer!, 0, widthBytes);
            Marshal.Copy(_videoRowBuffer!, 0, IntPtr.Add(destination, row * widthBytes), widthBytes);
            sourceRow = IntPtr.Add(sourceRow, sourceStep);
        }
    }

    private void EnsureVideoBuffer(int size)
    {
        if (_videoBuffer is null || _videoBuffer.Length < size)
        {
            _videoBuffer = new byte[size];
        }
    }

    private void EnsureVideoRowBuffer(int size)
    {
        if (_videoRowBuffer is null || _videoRowBuffer.Length < size)
        {
            _videoRowBuffer = new byte[size];
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _sinkWriter is null)
        {
            throw new InvalidOperationException("Sink writer is not initialized.");
        }
    }

    private void ReleaseSinkWriter()
    {
        if (_sinkWriter is not null)
        {
            Marshal.ReleaseComObject(_sinkWriter);
            _sinkWriter = null;
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null)
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private static void CheckHr(int hr, string operation)
    {
        if (hr != 0)
        {
            throw new COMException($"{operation} failed: 0x{hr:X8}", hr);
        }
    }

    private static Guid ResolveAudioSubtype(WaveFormat audioFormat)
    {
        return audioFormat.Encoding == WaveFormatEncoding.IeeeFloat
            ? MF.MFAudioFormat_Float
            : MF.MFAudioFormat_PCM;
    }

    private static int CalculateAudioBitrate(WaveFormat audioFormat)
    {
        var targetBitrate = audioFormat.Channels > 1 ? 192_000 : 128_000;
        return targetBitrate / 8;
    }

    private int CalculateVideoBitrate()
    {
        var raw = (long)_width * _height * _fps;
        raw = Math.Clamp(raw, 2_000_000L, 20_000_000L);
        return (int)raw;
    }

    private static long ToHns(long timestampMs)
    {
        return checked(timestampMs * 10_000);
    }
}
