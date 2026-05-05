# Screen Recorder (C# / WPF / .NET 8)

Windows 11 desktop screen recorder rebuilt in C# with a service-oriented architecture:

- **UI:** WPF dark theme (`#1a1a2e`, `#16213e`, `#00FF88`, `#ff4757`)
- **Audio:** WASAPI via **NAudio** (`WasapiLoopbackCapture`, `WasapiCapture`)
- **Capture backends:** Windows Graphics Capture detection with DXGI-designated fallback path
- **Encoding:** Media Foundation sink abstraction with **SharpAvi** MJPEG fallback

## Features

- Output path selection (Browse)
- FPS selection (10 / 15 / 20 / 30)
- Region selection via fullscreen transparent drag overlay
- Audio source mode: `None`, `System`, `Microphone`, `Both`
- Start/Stop controls and live timer
- Status with frame count and average FPS after save

## Project Layout

- `ScreenRecorder.sln` - Visual Studio solution
- `ScreenRecorder.Desktop/` - WPF project
  - `MainWindow` (UI)
  - `Services/RecorderService.cs` (capture/audio/encode orchestration)
  - `Capture/` (capture backends)
  - `Audio/` (WASAPI audio capture)
  - `Encoding/` (recording sinks)
  - `Views/RegionSelectorWindow` (drag-select overlay)

## NuGet Dependencies

- `NAudio`
- `SharpAvi`

## Build

From the repository root:

```powershell
dotnet restore
dotnet build ScreenRecorder.sln
```

## Run

```powershell
dotnet run --project .\ScreenRecorder.Desktop\ScreenRecorder.Desktop.csproj
```

## Notes

- Target framework: `net8.0-windows10.0.19041.0`
- Output container depends on sink selection:
  - Media Foundation sink when available
  - SharpAvi fallback writes **MJPEG + PCM AVI**
