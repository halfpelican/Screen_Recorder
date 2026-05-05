using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NAudio.Wave;
using SharpAvi;
using SharpAvi.Codecs;
using SharpAvi.Output;

namespace ScreenRecorder.Desktop.Encoding;

public sealed class SharpAviRecordingSink : IRecordingSink
{
    private readonly object gate = new();
    private AviWriter? writer;
    private IAviVideoStream? videoStream;
    private IAviAudioStream? audioStream;

    public string Name => "SharpAvi MJPEG AVI";
    public string OutputPath { get; private set; } = string.Empty;

    public void Initialize(string outputPath, int width, int height, int fps, WaveFormat? audioFormat)
    {
        OutputPath = Path.ChangeExtension(outputPath, ".avi");
        writer = new AviWriter(OutputPath)
        {
            FramesPerSecond = fps,
            EmitIndex1 = true
        };

        videoStream = writer.AddMotionJpegVideoStream(width, height, quality: 70);
        videoStream.Name = "Screen";

        if (audioFormat is not null)
        {
            audioStream = writer.AddAudioStream(audioFormat.Channels, audioFormat.SampleRate, audioFormat.BitsPerSample);
            audioStream.Name = "Audio";
        }

    }

    public void WriteVideoFrame(Bitmap frame, long timestampMs)
    {
        if (videoStream is null)
        {
            return;
        }

        using var memory = new MemoryStream();
        frame.Save(memory, ImageFormat.Jpeg);
        var data = memory.ToArray();

        lock (gate)
        {
            videoStream.WriteFrame(true, data, 0, data.Length);
        }
    }

    public void WriteAudioChunk(byte[] data, int count, long timestampMs)
    {
        if (audioStream is null || count <= 0)
        {
            return;
        }

        lock (gate)
        {
            audioStream.WriteBlock(data, 0, count);
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            writer?.Close();
            writer = null;
            videoStream = null;
            audioStream = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer?.Close();
            writer = null;
            videoStream = null;
            audioStream = null;
        }
    }
}
