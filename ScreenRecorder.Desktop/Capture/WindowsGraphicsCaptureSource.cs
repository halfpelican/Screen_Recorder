using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace ScreenRecorder.Desktop.Capture;

public sealed class WindowsGraphicsCaptureSource : BaseThreadedCaptureSource
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;

    private static readonly Guid GraphicsCaptureItemGuid = typeof(GraphicsCaptureItem).GUID;

    public override string Name => "Windows.Graphics.Capture";

    public override bool IsAvailable
    {
        get
        {
            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    return false;
                }

                var area = ResolveCaptureArea(null);
                var config = BuildCaptureConfiguration(area);
                using var runtime = CreateCaptureRuntime(config);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public override (int Width, int Height) GetFrameSize(Models.CaptureRegion? region)
    {
        var area = ResolveCaptureArea(region);
        var config = BuildCaptureConfiguration(area);
        using var runtime = CreateCaptureRuntime(config);
        return (config.Crop.Width, config.Crop.Height);
    }

    public override void RunCaptureLoop(
        Models.CaptureRegion? region,
        int fps,
        Stopwatch clock,
        Action<Bitmap, long> onFrame,
        CancellationToken cancellationToken)
    {
        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be greater than zero.");
        }

        CaptureRuntime runtime;
        try
        {
            var area = ResolveCaptureArea(region);
            var config = BuildCaptureConfiguration(area);
            runtime = CreateCaptureRuntime(config);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Windows.Graphics.Capture initialization failed: {ex.Message}", ex);
        }

        AutoResetEvent? frameEvent = null;
        Direct3D11CaptureFrame? latestFrame = null;
        var frameGate = new object();

        try
        {
            frameEvent = new AutoResetEvent(false);

            void OnFrameArrived(Direct3D11CaptureFramePool _, object _2)
            {
                lock (frameGate)
                {
                    latestFrame?.Dispose();
                    latestFrame = runtime.FramePool.TryGetNextFrame();
                }

                frameEvent.Set();
            }

            runtime.FramePool.FrameArrived += OnFrameArrived;

            using var cancellationRegistration = cancellationToken.Register(() => frameEvent.Set());
            runtime.Session.StartCapture();

            var frameInterval = TimeSpan.FromSeconds(1d / fps);
            var nextFrameAt = clock.Elapsed;

            while (!cancellationToken.IsCancellationRequested)
            {
                var now = clock.Elapsed;
                if (now < nextFrameAt)
                {
                    var delay = nextFrameAt - now;
                    if (delay.TotalMilliseconds > 1)
                    {
                        Thread.Sleep(delay);
                    }
                }

                if (!frameEvent.WaitOne(250))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Direct3D11CaptureFrame? frame;
                lock (frameGate)
                {
                    frame = latestFrame;
                    latestFrame = null;
                }

                if (frame is null)
                {
                    continue;
                }

                using (frame)
                {
                    var bitmap = FrameToBitmap(frame, runtime.Crop);
                    var timestampMs = (long)clock.Elapsed.TotalMilliseconds;
                    onFrame(bitmap, timestampMs);
                }

                nextFrameAt += frameInterval;
                if (nextFrameAt < clock.Elapsed - frameInterval)
                {
                    nextFrameAt = clock.Elapsed;
                }
            }

            runtime.FramePool.FrameArrived -= OnFrameArrived;
        }
        finally
        {
            lock (frameGate)
            {
                latestFrame?.Dispose();
            }

            frameEvent?.Dispose();
            runtime?.Dispose();
        }
    }

    private static CaptureRuntime CreateCaptureRuntime(CaptureConfiguration config)
    {
        var direct3DDevice = CreateDirect3DDevice();
        var item = CreateCaptureItem(config.MonitorHandle);
        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        var session = framePool.CreateCaptureSession(item);

        return new CaptureRuntime(direct3DDevice, item, framePool, session, config.Crop);
    }

    private static CaptureConfiguration BuildCaptureConfiguration(Rectangle area)
    {
        var center = new POINT
        {
            X = area.Left + area.Width / 2,
            Y = area.Top + area.Height / 2
        };

        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not resolve a monitor for Windows.Graphics.Capture.");
        }

        var monitorInfo = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>()
        };

        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to query monitor information.");
        }

        var monitorBounds = Rectangle.FromLTRB(
            monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Top,
            monitorInfo.rcMonitor.Right,
            monitorInfo.rcMonitor.Bottom);

        var relative = new Rectangle(
            area.Left - monitorBounds.Left,
            area.Top - monitorBounds.Top,
            area.Width,
            area.Height);

        var monitorSurface = new Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height);
        var crop = Rectangle.Intersect(relative, monitorSurface);
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("The requested capture region is outside the selected monitor.");
        }

        if (crop.Width != area.Width || crop.Height != area.Height)
        {
            throw new InvalidOperationException("Windows.Graphics.Capture source currently requires capture region to stay within one monitor.");
        }

        return new CaptureConfiguration(monitor, crop);
    }

    private static GraphicsCaptureItem CreateCaptureItem(IntPtr monitorHandle)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var createStringResult = WindowsCreateString(className, className.Length, out var classId);
        Marshal.ThrowExceptionForHR(createStringResult);

        IntPtr factoryPointer = IntPtr.Zero;

        var itemPointer = IntPtr.Zero;
        try
        {
            var factoryInterfaceId = typeof(IGraphicsCaptureItemInterop).GUID;
            var activationResult = RoGetActivationFactory(classId, ref factoryInterfaceId, out factoryPointer);
            Marshal.ThrowExceptionForHR(activationResult);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            var captureItemGuid = GraphicsCaptureItemGuid;
            var result = interop.CreateForMonitor(monitorHandle, ref captureItemGuid, out itemPointer);
            Marshal.ThrowExceptionForHR(result);

            return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPointer);
        }
        finally
        {
            if (factoryPointer != IntPtr.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            if (classId != IntPtr.Zero)
            {
                WindowsDeleteString(classId);
            }

            if (itemPointer != IntPtr.Zero)
            {
                Marshal.Release(itemPointer);
            }
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out var d3dDevicePointer,
            out _,
            out var d3dContextPointer);

        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverType.Warp,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out d3dDevicePointer,
                out _,
                out d3dContextPointer);
        }

        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var dxgiDeviceGuid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            var queryResult = Marshal.QueryInterface(d3dDevicePointer, ref dxgiDeviceGuid, out var dxgiDevicePointer);
            Marshal.ThrowExceptionForHR(queryResult);

            try
            {
                var interopResult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePointer, out var inspectablePointer);
                Marshal.ThrowExceptionForHR(interopResult);

                try
                {
                    return (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectablePointer);
                }
                finally
                {
                    Marshal.Release(inspectablePointer);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevicePointer);
            }
        }
        finally
        {
            if (d3dContextPointer != IntPtr.Zero)
            {
                Marshal.Release(d3dContextPointer);
            }

            if (d3dDevicePointer != IntPtr.Zero)
            {
                Marshal.Release(d3dDevicePointer);
            }
        }
    }

    private static Bitmap FrameToBitmap(Direct3D11CaptureFrame frame, Rectangle crop)
    {
        using var softwareBitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
        SoftwareBitmap? convertedBitmap = null;
        var bitmapForCopy = softwareBitmap;

        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
        {
            convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            bitmapForCopy = convertedBitmap;
        }

        try
        {
            var pixelWidth = bitmapForCopy.PixelWidth;
            var pixelHeight = bitmapForCopy.PixelHeight;
            var bytesPerPixel = 4;
            var fullBuffer = new byte[pixelWidth * pixelHeight * bytesPerPixel];
            bitmapForCopy.CopyToBuffer(fullBuffer.AsBuffer());

            using var fullBitmap = CreateBitmapFromRaw(pixelWidth, pixelHeight, fullBuffer);
            if (crop.X == 0 && crop.Y == 0 && crop.Width == pixelWidth && crop.Height == pixelHeight)
            {
                return (Bitmap)fullBitmap.Clone();
            }

            return fullBitmap.Clone(crop, PixelFormat.Format32bppArgb);
        }
        finally
        {
            convertedBitmap?.Dispose();
        }
    }

    private static Bitmap CreateBitmapFromRaw(int width, int height, byte[] bytes)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var sourceStride = width * 4;
            if (data.Stride == sourceStride)
            {
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        bytes,
                        row * sourceStride,
                        IntPtr.Add(data.Scan0, row * data.Stride),
                        sourceStride);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out D3DFeatureLevel featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid interfaceId, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid interfaceId, out IntPtr result);
    }

    private enum D3DDriverType : uint
    {
        Unknown = 0,
        Hardware = 1,
        Warp = 5
    }

    private enum D3DFeatureLevel : uint
    {
    }

    private sealed record CaptureConfiguration(IntPtr MonitorHandle, Rectangle Crop);

    private sealed class CaptureRuntime : IDisposable
    {
        public CaptureRuntime(
            IDirect3DDevice direct3DDevice,
            GraphicsCaptureItem item,
            Direct3D11CaptureFramePool framePool,
            GraphicsCaptureSession session,
            Rectangle crop)
        {
            Direct3DDevice = direct3DDevice;
            Item = item;
            FramePool = framePool;
            Session = session;
            Crop = crop;
        }

        public IDirect3DDevice Direct3DDevice { get; }
        public GraphicsCaptureItem Item { get; }
        public Direct3D11CaptureFramePool FramePool { get; }
        public GraphicsCaptureSession Session { get; }
        public Rectangle Crop { get; }

        public void Dispose()
        {
            Session.Dispose();
            FramePool.Dispose();
        }
    }
}
