using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.Wave;
using ScreenRecorder.Desktop.Encoding.Interop;

namespace ScreenRecorder.Desktop.Encoding;

/// <summary>
/// Records video and audio to an H.264/AAC MP4 file using Windows Media Foundation.
///
/// Video frames must be supplied as GDI+ <see cref="Bitmap"/> objects in any 32bpp format;
/// they are converted internally to NV12 (the native input of all Windows H.264 encoders)
/// before being handed to the sink writer.
///
/// Thread safety: all public methods are protected by an internal lock.
/// </summary>
public sealed class MediaFoundationMp4Sink : IRecordingSink
{
    private const int    MfVideoInterlaceProgressive = 2;
    private const int    AacLcProfile                = 0x29;
    private const long   HnsPerSecond               = 10_000_000;

    // H.264 encoders require dimensions that are multiples of 16.
    // Snapping only to even numbers is insufficient and causes MF_E_INVALIDMEDIATYPE.
    private const int H264MacroblockSize = 16;

    private readonly object _gate = new();
    private bool _disposed;
    private bool _initialized;
    private bool _mfStarted;

    private IMFSinkWriter? _sinkWriter;
    private int _videoStreamIndex;
    private int _audioStreamIndex = -1;

    private int  _width;
    private int  _height;
    private int  _fps;
    private long _frameDuration;
    private int  _audioBytesPerSecond;

    // Pre-allocated per-frame conversion buffers — avoids GC pressure on the hot path.
    private byte[]? _bgraRowBuffer;
    private byte[]? _nv12Buffer;

    /// <inheritdoc/>
    public string Name => "Media Foundation H.264 MP4";

    /// <inheritdoc/>
    public string OutputPath { get; private set; } = string.Empty;

    private MediaFoundationMp4Sink() { }

    // Finalizer as safety net in case Dispose() is not called.
    ~MediaFoundationMp4Sink() => Dispose(disposing: false);

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts to initialise the Media Foundation runtime and create a sink instance.
    /// Returns <c>false</c> and populates <paramref name="reason"/> on failure so the
    /// caller can fall back gracefully (e.g. to SharpAvi).
    /// </summary>
    public static bool TryCreate(out MediaFoundationMp4Sink? sink, out string reason)
    {
        sink   = null;
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

    // -------------------------------------------------------------------------
    // IRecordingSink
    // -------------------------------------------------------------------------

    /// <summary>
    /// Configures all Media Foundation streams and begins writing.
    /// Must be called exactly once before any Write* calls.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called more than once.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is null or empty.</exception>
    /// <exception cref="COMException">A Media Foundation call failed.</exception>
    public void Initialize(string outputPath, int width, int height, int fps, WaveFormat? audioFormat)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_initialized)
                throw new InvalidOperationException("Already initialized.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            OutputPath = outputPath;

            // Snap dimensions up to the nearest multiple of 16.
            // H.264 operates on 16×16 macroblocks; supplying any other size causes
            // MF_E_INVALIDMEDIATYPE (0xC00D36B4) regardless of input format.
            _width  = RoundUpToMultiple(width,  H264MacroblockSize);
            _height = RoundUpToMultiple(height, H264MacroblockSize);
            _fps    = fps;
            _frameDuration = HnsPerSecond / Math.Max(1, fps);

            // NV12: Y plane (w×h bytes) + interleaved UV plane (w×h/2 bytes)
            _nv12Buffer    = new byte[_width * _height * 3 / 2];
            _bgraRowBuffer = new byte[_width * 4];

            try
            {
                CreateSinkWriter(outputPath);
                ConfigureVideoStream();

                if (audioFormat is not null)
                    ConfigureAudioStream(audioFormat);

                CheckHr(_sinkWriter!.BeginWriting(), "IMFSinkWriter::BeginWriting");
                _initialized = true;
            }
            catch
            {
                // Roll back any partial initialisation so the object is left in a
                // consistent state even if the caller ignores the exception.
                ReleaseSinkWriter();
                throw;
            }
        }
    }

    /// <summary>
    /// Encodes and writes one video frame.
    /// <paramref name="frame"/> is not disposed by this method.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="frame"/> dimensions do not match the dimensions supplied to
    /// <see cref="Initialize"/>. The sink writer cannot resize frames mid-recording.
    /// </exception>
    public void WriteVideoFrame(Bitmap frame, long timestampMs)
    {
        lock (_gate)
        {
            EnsureReady();

            IMFSample?      sample   = null;
            IMFMediaBuffer? buffer   = null;
            Bitmap?         prepared = null;
            BitmapData?     bits     = null;

            try
            {
                prepared = EnsureCorrectFormat(frame);
                bits     = prepared.LockBits(
                               new Rectangle(0, 0, _width, _height),
                               ImageLockMode.ReadOnly,
                               PixelFormat.Format32bppArgb);

                ConvertBgraToNv12(bits);

                int nv12Size = _nv12Buffer!.Length;
                CheckHr(MF.MFCreateMemoryBuffer(nv12Size, out buffer), "MFCreateMemoryBuffer");

                CheckHr(buffer.Lock(out var dst, out _, out _), "IMFMediaBuffer::Lock");
                try   { Marshal.Copy(_nv12Buffer, 0, dst, nv12Size); }
                finally { buffer.Unlock(); }

                CheckHr(buffer.SetCurrentLength(nv12Size),          "SetCurrentLength");
                CheckHr(MF.MFCreateSample(out sample),              "MFCreateSample");
                CheckHr(sample.AddBuffer(buffer),                   "AddBuffer");
                CheckHr(sample.SetSampleTime(ToHns(timestampMs)),   "SetSampleTime");
                CheckHr(sample.SetSampleDuration(_frameDuration),   "SetSampleDuration");
                CheckHr(_sinkWriter!.WriteSample(_videoStreamIndex, sample), "WriteSample(video)");
            }
            finally
            {
                // COM objects must be released last, after all managed cleanup.
                if (bits is not null && prepared is not null)
                    prepared.UnlockBits(bits);

                if (prepared is not null && !ReferenceEquals(prepared, frame))
                    prepared.Dispose();

                ReleaseComObject(ref sample);
                ReleaseComObject(ref buffer);
            }
        }
    }

    /// <summary>
    /// Writes a chunk of raw PCM or IEEE-float audio data.
    /// No-ops silently if audio was not configured in <see cref="Initialize"/>.
    /// </summary>
    public void WriteAudioChunk(byte[] data, int count, long timestampMs)
    {
        if (count <= 0) return;

        lock (_gate)
        {
            EnsureReady();
            if (_audioStreamIndex < 0 || _audioBytesPerSecond <= 0) return;

            IMFSample?      sample = null;
            IMFMediaBuffer? buffer = null;

            try
            {
                CheckHr(MF.MFCreateMemoryBuffer(count, out buffer), "MFCreateMemoryBuffer(audio)");

                CheckHr(buffer.Lock(out var dst, out _, out _), "IMFMediaBuffer::Lock(audio)");
                try   { Marshal.Copy(data, 0, dst, count); }
                finally { buffer.Unlock(); }

                CheckHr(buffer.SetCurrentLength(count),             "SetCurrentLength(audio)");
                CheckHr(MF.MFCreateSample(out sample),              "MFCreateSample(audio)");
                CheckHr(sample.AddBuffer(buffer),                   "AddBuffer(audio)");
                CheckHr(sample.SetSampleTime(ToHns(timestampMs)),   "SetSampleTime(audio)");

                long duration = (long)Math.Round(count * (double)HnsPerSecond / _audioBytesPerSecond);
                if (duration > 0)
                    CheckHr(sample.SetSampleDuration(duration), "SetSampleDuration(audio)");

                CheckHr(_sinkWriter!.WriteSample(_audioStreamIndex, sample), "WriteSample(audio)");
            }
            finally
            {
                ReleaseComObject(ref sample);
                ReleaseComObject(ref buffer);
            }
        }
    }

    /// <summary>
    /// Flushes all pending samples and finalises the MP4 container.
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            try
            {
                // MFInvokeFinalize is the aliased IMFSinkWriter::Finalize — see interop comments.
                CheckHr(_sinkWriter!.MFInvokeFinalize(), "IMFSinkWriter::Finalize");
            }
            finally
            {
                ReleaseSinkWriter();
                _initialized = false;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (_initialized)
                Complete();

            if (_mfStarted)
            {
                MF.MFShutdown();
                _mfStarted = false;
            }

            _disposed = true;
        }
    }

    // -------------------------------------------------------------------------
    // Sink writer creation
    // -------------------------------------------------------------------------

    private void CreateSinkWriter(string outputPath)
    {
        IMFAttributes? writerAttrs = null;
        try
        {
            CheckHr(MF.MFCreateAttributes(out writerAttrs, 1), "MFCreateAttributes(writer)");

            // Allow the writer to insert software/hardware transform DMOs for audio encoding.
            CheckHr(writerAttrs.SetUINT32(MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1),
                    "Set MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS");

            CheckHr(MF.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, writerAttrs, out _sinkWriter),
                    "MFCreateSinkWriterFromURL");
        }
        finally
        {
            ReleaseComObject(ref writerAttrs);
        }
    }

    // -------------------------------------------------------------------------
    // Stream configuration
    // -------------------------------------------------------------------------

    private void ConfigureVideoStream()
    {
        IMFMediaType? outputType = null;
        IMFMediaType? inputType  = null;

        try
        {
            // Output: H.264 compressed elementary stream
            CheckHr(MF.MFCreateMediaType(out outputType),                                               "MFCreateMediaType(vout)");
            CheckHr(outputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video),                     "vout: major type");
            CheckHr(outputType.SetGUID(MF.MF_MT_SUBTYPE,    MF.MFVideoFormat_H264),                    "vout: subtype");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AVG_BITRATE,    CalculateVideoBitrate()),            "vout: bitrate");
            CheckHr(outputType.SetUINT32(MF.MF_MT_INTERLACE_MODE, MfVideoInterlaceProgressive),        "vout: interlace");
            CheckHr(MF.MFSetAttributeSize(outputType,  MF.MF_MT_FRAME_SIZE,         (uint)_width, (uint)_height), "vout: frame size");
            CheckHr(MF.MFSetAttributeRatio(outputType, MF.MF_MT_FRAME_RATE,         (uint)_fps, 1),               "vout: frame rate");
            CheckHr(MF.MFSetAttributeRatio(outputType, MF.MF_MT_PIXEL_ASPECT_RATIO, 1, 1),                        "vout: PAR");
            CheckHr(_sinkWriter!.AddStream(outputType, out _videoStreamIndex),                         "AddStream(video)");

            // Input: NV12 planar YUV 4:2:0
            // All Windows H.264 encoders (software MSH264ENC and hardware MFTs) declare
            // NV12 as their only accepted input format. Providing it directly avoids the
            // colour-converter DMO chain that caused MF_E_INVALIDMEDIATYPE with RGB32.
            // BGRA→NV12 conversion is performed in ConvertBgraToNv12 below.
            CheckHr(MF.MFCreateMediaType(out inputType),                                                "MFCreateMediaType(vin)");
            CheckHr(inputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video),                      "vin: major type");
            CheckHr(inputType.SetGUID(MF.MF_MT_SUBTYPE,    MF.MFVideoFormat_NV12),                     "vin: subtype");
            CheckHr(inputType.SetUINT32(MF.MF_MT_INTERLACE_MODE, MfVideoInterlaceProgressive),         "vin: interlace");
            CheckHr(MF.MFSetAttributeSize(inputType,  MF.MF_MT_FRAME_SIZE, (uint)_width, (uint)_height), "vin: frame size");
            CheckHr(MF.MFSetAttributeRatio(inputType, MF.MF_MT_FRAME_RATE, (uint)_fps, 1),               "vin: frame rate");
            CheckHr(_sinkWriter!.SetInputMediaType(_videoStreamIndex, inputType, null),                 "SetInputMediaType(video)");
        }
        finally
        {
            ReleaseComObject(ref inputType);
            ReleaseComObject(ref outputType);
        }
    }

    private void ConfigureAudioStream(WaveFormat fmt)
    {
        IMFMediaType? outputType = null;
        IMFMediaType? inputType  = null;

        try
        {
            _audioBytesPerSecond = fmt.AverageBytesPerSecond;

            CheckHr(MF.MFCreateMediaType(out outputType),                                                   "MFCreateMediaType(aout)");
            CheckHr(outputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio),                         "aout: major");
            CheckHr(outputType.SetGUID(MF.MF_MT_SUBTYPE,    MF.MFAudioFormat_AAC),                         "aout: subtype");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS,         fmt.Channels),               "aout: channels");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND,   fmt.SampleRate),             "aout: sample rate");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE,      16),                         "aout: bits");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT,      1),                          "aout: block align");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, CalculateAudioBitrate(fmt)), "aout: avg bytes");
            CheckHr(outputType.SetUINT32(MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, AacLcProfile),       "aout: AAC profile");
            CheckHr(_sinkWriter!.AddStream(outputType, out _audioStreamIndex),                             "AddStream(audio)");

            CheckHr(MF.MFCreateMediaType(out inputType),                                                    "MFCreateMediaType(ain)");
            CheckHr(inputType.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio),                          "ain: major");
            CheckHr(inputType.SetGUID(MF.MF_MT_SUBTYPE,    ResolveAudioSubtype(fmt)),                      "ain: subtype");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS,         fmt.Channels),               "ain: channels");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND,   fmt.SampleRate),             "ain: sample rate");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE,      fmt.BitsPerSample),          "ain: bits");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT,      fmt.BlockAlign),             "ain: block align");
            CheckHr(inputType.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, fmt.AverageBytesPerSecond),  "ain: avg bytes");
            CheckHr(_sinkWriter!.SetInputMediaType(_audioStreamIndex, inputType, null),                    "SetInputMediaType(audio)");
        }
        finally
        {
            ReleaseComObject(ref inputType);
            ReleaseComObject(ref outputType);
        }
    }

    // -------------------------------------------------------------------------
    // BGRA → NV12 conversion
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <paramref name="frame"/> unchanged if it already matches the aligned
    /// dimensions and pixel format, otherwise returns a new aligned 32bpp ARGB bitmap.
    /// If the source frame is smaller than aligned dimensions, the image is copied
    /// at the top-left and the padding area is filled black to preserve full content.
    /// The caller is responsible for disposing the returned bitmap if it differs
    /// from <paramref name="frame"/>.
    /// </summary>
    private Bitmap EnsureCorrectFormat(Bitmap frame)
    {
        if (frame.Width == _width &&
            frame.Height == _height &&
            frame.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return frame;
        }

        if (frame.Width > _width || frame.Height > _height)
        {
            throw new ArgumentException(
                $"Frame size {frame.Width}×{frame.Height} exceeds aligned encoder frame {_width}×{_height}.",
                nameof(frame));
        }

        var converted = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(converted);
        g.Clear(Color.Black);
        g.DrawImage(
            frame,
            new Rectangle(0, 0, frame.Width, frame.Height),
            new Rectangle(0, 0, frame.Width, frame.Height),
            GraphicsUnit.Pixel);
        return converted;
    }

    /// <summary>
    /// Converts a locked BGRA (GDI+ <see cref="PixelFormat.Format32bppArgb"/>) bitmap
    /// into NV12 planar format in <see cref="_nv12Buffer"/>.
    ///
    /// NV12 memory layout:
    /// <list type="bullet">
    ///   <item>Bytes [0 .. W×H)       — Y luma plane, one byte per pixel</item>
    ///   <item>Bytes [W×H .. W×H×3/2) — Interleaved UV plane, one Cb+Cr pair per 2×2 block</item>
    /// </list>
    ///
    /// Uses BT.601 limited-range integer fixed-point coefficients (shift-by-8):
    /// <code>
    ///   Y  = (( 66·R + 129·G +  25·B + 128) >> 8) + 16
    ///   Cb = ((-38·R -  74·G + 112·B + 128) >> 8) + 128
    ///   Cr = ((112·R -  94·G -  18·B + 128) >> 8) + 128
    /// </code>
    /// </summary>
    private void ConvertBgraToNv12(BitmapData bits)
    {
        int uvOffset = _width * _height;

        for (int row = 0; row < _height; row++)
        {
            Marshal.Copy(
                IntPtr.Add(bits.Scan0, row * bits.Stride),
                _bgraRowBuffer!, 0, _width * 4);

            int yBase  = row * _width;
            int uvBase = uvOffset + (row >> 1) * _width;

            for (int col = 0; col < _width; col++)
            {
                int px = col * 4;
                int b  = _bgraRowBuffer![px];
                int g  = _bgraRowBuffer[px + 1];
                int r  = _bgraRowBuffer[px + 2];
                // Alpha channel (px+3) is irrelevant for screen capture.

                _nv12Buffer![yBase + col] =
                    (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                // UV sub-sampled 2:1 in both axes — write once per 2×2 pixel block.
                if ((row & 1) == 0 && (col & 1) == 0)
                {
                    _nv12Buffer[uvBase + col]     =
                        (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128); // Cb (U)
                    _nv12Buffer[uvBase + col + 1] =
                        (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);  // Cr (V)
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void EnsureReady()
    {
        ThrowIfDisposed();
        if (!_initialized || _sinkWriter is null)
            throw new InvalidOperationException("Sink writer is not initialized. Call Initialize() first.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MediaFoundationMp4Sink));
    }

    private void ReleaseSinkWriter()
    {
        if (_sinkWriter is not null)
        {
            Marshal.ReleaseComObject(_sinkWriter);
            _sinkWriter = null;
        }
    }

    // Generic ref overload prevents accidentally passing a wrong local variable.
    private static void ReleaseComObject<T>(ref T? obj) where T : class
    {
        if (obj is not null)
        {
            Marshal.ReleaseComObject(obj);
            obj = null;
        }
    }

    private static void CheckHr(int hr, string operation)
    {
        if (hr != 0)
            throw new COMException($"Media Foundation: {operation} failed (0x{hr:X8})", hr);
    }

    private static int RoundUpToMultiple(int value, int multiple) =>
        ((value + multiple - 1) / multiple) * multiple;

    private static Guid ResolveAudioSubtype(WaveFormat fmt) =>
        fmt.Encoding == WaveFormatEncoding.IeeeFloat
            ? MF.MFAudioFormat_Float
            : MF.MFAudioFormat_PCM;

    private static int CalculateAudioBitrate(WaveFormat fmt) =>
        (fmt.Channels > 1 ? 192_000 : 128_000) / 8;

    private int CalculateVideoBitrate() =>
        (int)Math.Clamp((long)_width * _height * _fps, 2_000_000L, 20_000_000L);

    private static long ToHns(long ms) => checked(ms * 10_000);
}
