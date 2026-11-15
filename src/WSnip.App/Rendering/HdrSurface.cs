using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Windows.Graphics.DirectX;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Windows;

namespace WSnip.App.Rendering;

public readonly record struct SurfaceColorInfo(bool ExtendedRange, double ReferenceWhiteScale, double Headroom);

/// <summary>
/// Presents premultiplied FP16 scRGB pixels in a WinUI composition swap chain. Skia draws in
/// relative linear light; only this boundary converts SDR white to Windows' absolute 80-nit units.
/// A device without FP16 support receives an explicitly SDR sRGB surface.
/// </summary>
public class HdrSurface : Grid, IDisposable
{
    private readonly CanvasSwapChainPanel panel = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CanvasDevice? device;
    private CanvasSwapChain? swapChain;
    private CanvasBitmap? bitmap;
    private SKSurface? raster;
    private SKColorSpace? colorSpace;
    private byte[]? pixels;
    private int width, height;
    private double pixelScale;
    private bool queued, disposed, allowFloat = true;
    private DisplayColorInfo display = DisplayColorInfo.Sdr;
    private XamlRoot? observedRoot;
    private bool failed;

    public HdrSurface()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(panel);
        SizeChanged += (_, _) => InvalidateVisual();
        ActualThemeChanged += (_, _) => InvalidateVisual();
        Loaded += (_, _) =>
        {
            observedRoot = XamlRoot;
            if (observedRoot is not null) observedRoot.Changed += RootChanged;
            RefreshDisplay();
            displayTimer.Start();
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            displayTimer.Stop();
            if (observedRoot is not null) observedRoot.Changed -= RootChanged;
            observedRoot = null;
            ReleaseBuffers();
        };
        displayTimer.Tick += (_, _) => RefreshDisplay();
    }
    public nint WindowHandle { get; set; }
    public DisplayColorInfo? FixedDisplay { get; set; }
    public SurfaceColorInfo Surface { get; private set; }
    public event EventHandler? SurfaceChanged;
    public event EventHandler<Exception>? RenderingFailed;
    protected double RasterScale => XamlRoot?.RasterizationScale ?? 1;
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs e) => InvalidateVisual();
    private void RefreshDisplay()
    {
        var next = FixedDisplay ?? (WindowHandle == 0 ? DisplayColorInfo.Sdr : WindowsDisplayState.ForWindow(WindowHandle));
        if (display == next) return;
        display = next;
        ReleaseBuffers();
        InvalidateVisual();
    }
    public void InvalidateVisual()
    {
        if (queued || disposed || failed) return;
        queued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            queued = false;
            if (disposed || !IsLoaded || Visibility != Visibility.Visible || ActualWidth <= 0 || ActualHeight <= 0) return;
            try { DrawFrame(); }
            catch (COMException exception) when (device?.IsDeviceLost(exception.HResult) == true)
            {
                ReleaseBuffers();
                device?.Dispose();
                device = null;
                InvalidateVisual();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failed = true;
                LightStudio.Logging.AppLog.Error("Rendering", "Presenting the image failed.", exception);
                RenderingFailed?.Invoke(this, exception);
            }
        });
    }
    private void DrawFrame()
    {
        double scale = RasterScale;
        int w = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        device ??= new CanvasDevice();
        bool fp16 = allowFloat && device.IsPixelFormatSupported(DirectXPixelFormat.R16G16B16A16Float);
        var info = new SurfaceColorInfo(fp16 && display.AdvancedColor, fp16 ? display.WhiteScale : 1, fp16 ? display.HeadroomRatio : 1);
        if (swapChain is null || width != w || height != h || pixelScale != scale || Surface != info)
        {
            ReleaseBuffers();
            width = w; height = h; pixelScale = scale;
            var format = fp16 ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            try { swapChain = new CanvasSwapChain(device, (float)(w / scale), (float)(h / scale), (float)(96 * scale), format, 2, CanvasAlphaMode.Premultiplied); }
            catch (COMException) when (fp16)
            {
                allowFloat = false;
                ReleaseBuffers();
                DrawFrame();
                return;
            }
            panel.SwapChain = swapChain;
            pixels = GC.AllocateUninitializedArray<byte>(checked(w * h * (fp16 ? 8 : 4)), pinned: true);
            if (fp16)
            {
                float k = (float)(1 / info.ReferenceWhiteScale);
                float[] m = SKColorSpaceXyz.Srgb.Values;
                colorSpace = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear,
                    new SKColorSpaceXyz(m[0]*k,m[1]*k,m[2]*k,m[3]*k,m[4]*k,m[5]*k,m[6]*k,m[7]*k,m[8]*k));
            }
            else colorSpace = SKColorSpace.CreateSrgb();
            var skInfo = new SKImageInfo(w, h, fp16 ? SKColorType.RgbaF16 : SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace);
            raster = SKSurface.Create(skInfo, Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0), skInfo.RowBytes)
                ?? throw new InvalidOperationException("The image presentation surface could not be created.");
            bitmap = CanvasBitmap.CreateFromBytes(device, pixels, w, h, format, (float)(96 * scale), CanvasAlphaMode.Premultiplied);
            if (Surface != info) { Surface = info; SurfaceChanged?.Invoke(this, EventArgs.Empty); }
        }
        var canvas = raster!.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        try
        {
            canvas.Scale((float)scale);
            Draw(canvas);
        }
        finally { canvas.Restore(); }
        canvas.Flush();
        bitmap!.SetPixelBytes(pixels!);
        using (var ds = swapChain.CreateDrawingSession(Microsoft.UI.Colors.Transparent)) ds.DrawImage(bitmap);
        swapChain.Present();
    }
    protected virtual void Draw(SKCanvas canvas) { }
    public void ReleaseBuffers()
    {
        panel.SwapChain = null;
        bitmap?.Dispose(); bitmap = null;
        swapChain?.Dispose(); swapChain = null;
        raster?.Dispose(); raster = null;
        colorSpace?.Dispose(); colorSpace = null;
        pixels = null;
    }
    public virtual void Dispose()
    {
        if (disposed) return;
        disposed = true;
        displayTimer.Stop();
        if (observedRoot is not null) observedRoot.Changed -= RootChanged;
        observedRoot = null;
        ReleaseBuffers();
        device?.Dispose(); device = null;
    }
}
