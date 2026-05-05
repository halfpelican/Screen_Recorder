using System.Drawing;
using NAudio.Wave;

namespace ScreenRecorder.Desktop.Encoding;

public interface IRecordingSink : IDisposable
{
    string Name { get; }
    string OutputPath { get; }

    void Initialize(string outputPath, int width, int height, int fps, WaveFormat? audioFormat);

    void WriteVideoFrame(Bitmap frame, long timestampMs);

    void WriteAudioChunk(byte[] data, int count, long timestampMs);

    void Complete();
}
