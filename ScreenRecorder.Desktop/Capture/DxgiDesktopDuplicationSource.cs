using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenRecorder.Desktop.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ScreenRecorder.Desktop.Capture;

public sealed class DxgiDesktopDuplicationSource : BaseThreadedCaptureSource
{
    private const int BytesPerPixel = 4;
    private static readonly FeatureLevel[] DeviceFeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    ];

    private readonly object sync = new();
    private ID3D11Device? device;
    private ID3D11DeviceContext? deviceContext;
    private IDXGIOutputDuplication? duplication;
    private ID3D11Texture2D? stagingTexture;
    private Rectangle outputBounds;
    private Rectangle captureArea;
    private bool initialized;
    private bool disposed;
    private Bitmap? lastFrame;

    public override string Name => "DXGI Desktop Duplication";
    public override bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public override (int Width, int Height) GetFrameSize(CaptureRegion? region)
    {
        var area = ResolveEffectiveCaptureArea(region);
        return (area.Width, area.Height);
    }

    protected override Bitmap CaptureFrame(Rectangle area)
    {
        return CaptureDxgiFrame(area);
    }

    public override void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ReleaseResources();
        }
    }

    private Bitmap CaptureDxgiFrame(Rectangle area)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            Initialize(area);

            if (deviceContext is null || duplication is null || stagingTexture is null)
            {
                throw new InvalidOperationException("DXGI duplication is not initialized.");
            }

            bool frameAcquired = false;
            IDXGIResource? desktopResource = null;

            try
            {
                var acquireResult = duplication.AcquireNextFrame(33, out _, out desktopResource);
                if (acquireResult == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    if (lastFrame is null)
                    {
                        return new Bitmap(captureArea.Width, captureArea.Height, PixelFormat.Format32bppArgb);
                    }

                    return (Bitmap)lastFrame.Clone();
                }

                acquireResult.CheckError();
                frameAcquired = true;

                using var texture = desktopResource.QueryInterfaceOrNull<ID3D11Texture2D>()
                    ?? throw new InvalidOperationException("Unable to access duplicated desktop texture.");

                deviceContext.CopyResource(stagingTexture, texture);
                var mapped = deviceContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    var bitmap = CopyMappedTextureToBitmap(mapped);
                    lastFrame?.Dispose();
                    lastFrame = (Bitmap)bitmap.Clone();
                    return bitmap;
                }
                finally
                {
                    deviceContext.Unmap(stagingTexture, 0);
                }
            }
            catch (COMException ex) when (IsDuplicationResetRequired((uint)ex.HResult))
            {
                Reinitialize();
                return lastFrame is null
                    ? new Bitmap(captureArea.Width, captureArea.Height, PixelFormat.Format32bppArgb)
                    : (Bitmap)lastFrame.Clone();
            }
            finally
            {
                desktopResource?.Dispose();
                if (frameAcquired)
                {
                    duplication.ReleaseFrame();
                }
            }
        }
    }

    private Bitmap CopyMappedTextureToBitmap(MappedSubresource mapped)
    {
        var bitmap = new Bitmap(captureArea.Width, captureArea.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            var sourceX = (captureArea.Left - outputBounds.Left) * BytesPerPixel;
            var sourceY = captureArea.Top - outputBounds.Top;
            var rowBytes = captureArea.Width * BytesPerPixel;

            for (var row = 0; row < captureArea.Height; row++)
            {
                var sourceRowOffset = (sourceY + row) * mapped.RowPitch + sourceX;
                var sourceRow = IntPtr.Add(mapped.DataPointer, checked((int)sourceRowOffset));
                var destinationRow = IntPtr.Add(bits.Scan0, row * bits.Stride);
                CopyPixels(sourceRow, destinationRow, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }

        return bitmap;
    }

    private static void CopyPixels(IntPtr source, IntPtr destination, int rowBytes)
    {
        var buffer = new byte[rowBytes];
        Marshal.Copy(source, buffer, 0, rowBytes);
        Marshal.Copy(buffer, 0, destination, rowBytes);
    }

    private Rectangle ResolveEffectiveCaptureArea(CaptureRegion? region)
    {
        var requestedArea = ResolveCaptureArea(region);
        if (!TrySelectOutput(requestedArea, out var selected) || selected is null)
        {
            throw new InvalidOperationException("No desktop output is available for DXGI duplication.");
        }

        using (selected)
        {
            if (region is null || !region.IsValid)
            {
                return selected.Bounds;
            }

            var intersection = Rectangle.Intersect(requestedArea, selected.Bounds);
            return intersection.Width > 0 && intersection.Height > 0
                ? intersection
                : selected.Bounds;
        }
    }

    private void Initialize(Rectangle area)
    {
        lock (sync)
        {
            ThrowIfDisposed();

            if (initialized && captureArea == area)
            {
                return;
            }

            ReleaseResources();

            if (!TrySelectOutput(area, out var selectedOutput) || selectedOutput is null)
            {
                throw new InvalidOperationException("Unable to locate a desktop output for DXGI duplication.");
            }

            using (selectedOutput)
            {
                var createResult = D3D11CreateDevice(
                    selectedOutput.Adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    DeviceFeatureLevels,
                    out var localDevice,
                    out _,
                    out var localContext);
                createResult.CheckError();

                using var output1 = selectedOutput.Output.QueryInterfaceOrNull<IDXGIOutput1>()
                    ?? throw new InvalidOperationException("Selected output does not support DXGI output duplication.");

                var localDuplication = output1.DuplicateOutput(localDevice);

                var stagingDescription = new Texture2DDescription
                {
                    Width = (uint)selectedOutput.Bounds.Width,
                    Height = (uint)selectedOutput.Bounds.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };

                var localStaging = localDevice.CreateTexture2D(stagingDescription);

                outputBounds = selectedOutput.Bounds;
                captureArea = Rectangle.Intersect(area, outputBounds);
                if (captureArea.Width <= 0 || captureArea.Height <= 0)
                {
                    captureArea = outputBounds;
                }

                device = localDevice;
                deviceContext = localContext;
                duplication = localDuplication;
                stagingTexture = localStaging;
                initialized = true;
            }
        }
    }

    private void Reinitialize()
    {
        if (captureArea.Width <= 0 || captureArea.Height <= 0)
        {
            throw new InvalidOperationException("Cannot reinitialize DXGI duplication without a valid capture area.");
        }

        initialized = false;
        Initialize(captureArea);
    }

    private void ReleaseResources()
    {
        lastFrame?.Dispose();
        lastFrame = null;

        stagingTexture?.Dispose();
        stagingTexture = null;

        duplication?.Dispose();
        duplication = null;

        deviceContext?.Dispose();
        deviceContext = null;

        device?.Dispose();
        device = null;

        outputBounds = Rectangle.Empty;
        captureArea = Rectangle.Empty;
        initialized = false;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(DxgiDesktopDuplicationSource));
        }
    }

    private static bool IsDuplicationResetRequired(uint hResult) =>
        hResult is 0x887A0026 or 0x887A0027;

    private static bool TrySelectOutput(Rectangle targetArea, out OutputSelection? selection)
    {
        selection = null;
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        uint? bestAdapterIndex = null;
        uint? bestOutputIndex = null;
        Rectangle bestBounds = Rectangle.Empty;
        var bestIntersectionArea = int.MinValue;

        uint? primaryAdapterIndex = null;
        uint? primaryOutputIndex = null;
        Rectangle primaryBounds = Rectangle.Empty;

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Failure)
            {
                break;
            }

            using (adapter)
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        var description = output.Description;
                        if (!description.AttachedToDesktop)
                        {
                            continue;
                        }

                        var bounds = new Rectangle(
                            description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Top,
                            description.DesktopCoordinates.Right - description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top);

                        if (bounds.Width <= 0 || bounds.Height <= 0)
                        {
                            continue;
                        }

                        var intersection = Rectangle.Intersect(bounds, targetArea);
                        var intersectionArea = intersection.Width * intersection.Height;
                        if (intersectionArea > bestIntersectionArea)
                        {
                            bestIntersectionArea = intersectionArea;
                            bestAdapterIndex = adapterIndex;
                            bestOutputIndex = outputIndex;
                            bestBounds = bounds;
                        }

                        if (primaryAdapterIndex is null && bounds.Contains(0, 0))
                        {
                            primaryAdapterIndex = adapterIndex;
                            primaryOutputIndex = outputIndex;
                            primaryBounds = bounds;
                        }
                    }
                }
            }
        }

        if (bestAdapterIndex is null || bestOutputIndex is null)
        {
            if (primaryAdapterIndex is null || primaryOutputIndex is null)
            {
                return false;
            }

            bestAdapterIndex = primaryAdapterIndex;
            bestOutputIndex = primaryOutputIndex;
            bestBounds = primaryBounds;
        }

        var selectedAdapterResult = factory.EnumAdapters1(bestAdapterIndex.Value, out var selectedAdapter);
        if (selectedAdapterResult.Failure)
        {
            selectedAdapter?.Dispose();
            return false;
        }

        var selectedOutputResult = selectedAdapter.EnumOutputs(bestOutputIndex.Value, out var selectedOutput);
        if (selectedOutputResult.Failure)
        {
            selectedOutput?.Dispose();
            selectedAdapter.Dispose();
            return false;
        }

        selection = new OutputSelection(selectedAdapter, selectedOutput, bestBounds);
        return true;
    }

    private sealed class OutputSelection(
        IDXGIAdapter1 adapter,
        IDXGIOutput output,
        Rectangle bounds) : IDisposable
    {
        public IDXGIAdapter1 Adapter { get; } = adapter;
        public IDXGIOutput Output { get; } = output;
        public Rectangle Bounds { get; } = bounds;

        public void Dispose()
        {
            Output?.Dispose();
            Adapter?.Dispose();
        }
    }
}
