namespace ScreenRecorder.Desktop.Models;

public sealed record CaptureRegion(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;

    public override string ToString()
    {
        return $"{Width}x{Height} @ ({Left}, {Top})";
    }
}
