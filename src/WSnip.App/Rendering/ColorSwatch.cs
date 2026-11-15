using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using WSnip.Core.Imaging;
using WSnip.Windows;
using CompositionFormat = Microsoft.Graphics.DirectX.DirectXPixelFormat;
using CompositionAlpha = Microsoft.Graphics.DirectX.DirectXAlphaMode;

namespace WSnip.App.Rendering;

/// <summary>A small FP16 composition surface shares XAML clipping, popups and acrylic correctly.</summary>
public sealed class ColorSwatch : Grid, IDisposable
{
    private ScRgb color = ScRgb.White;
    private CanvasDevice? device;
    private CompositionGraphicsDevice? graphics;
    private CompositionDrawingSurface? surface;
    private CompositionSurfaceBrush? surfaceBrush;
    private SpriteVisual? visual;
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private double whiteScale = 1;
    private bool extended;
    private bool disposed;
    public nint WindowHandle { get; set; }
    public ScRgb Color { get => color; set { color = value; Redraw(); } }
    public ColorSwatch()
    {
        Loaded += (_, _) => { CreateSurface(); displayTimer.Start(); };
        Unloaded += (_, _) => { displayTimer.Stop(); ReleaseResources(); };
        SizeChanged += (_, _) => { if (IsLoaded) CreateSurface(); };
        displayTimer.Tick += (_, _) =>
        {
            var info = WindowsDisplayState.ForWindow(WindowHandle);
            if (whiteScale != info.WhiteScale || extended != info.AdvancedColor) Redraw();
        };
    }
    private void CreateSurface()
    {
        if (disposed || ActualWidth <= 0 || ActualHeight <= 0) return;
        ReleaseResources();
        device = CanvasDevice.GetSharedDevice();
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor,device);
        double scale = XamlRoot?.RasterizationScale ?? 1;
        bool fp16 = device.IsPixelFormatSupported(global::Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float);
        try { surface = graphics.CreateDrawingSurface(new Size(Math.Ceiling(ActualWidth*scale),Math.Ceiling(ActualHeight*scale)),fp16 ? CompositionFormat.R16G16B16A16Float : CompositionFormat.B8G8R8A8UIntNormalized,CompositionAlpha.Premultiplied); }
        catch (System.Runtime.InteropServices.COMException) when (fp16)
        {
            surface = graphics.CreateDrawingSurface(new Size(Math.Ceiling(ActualWidth*scale),Math.Ceiling(ActualHeight*scale)),CompositionFormat.B8G8R8A8UIntNormalized,CompositionAlpha.Premultiplied);
        }
        surfaceBrush = compositor.CreateSurfaceBrush(surface);
        visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2((float)ActualWidth,(float)ActualHeight);
        visual.Brush = surfaceBrush;
        ElementCompositionPreview.SetElementChildVisual(this,visual);
        Redraw();
    }
    private void Redraw()
    {
        if (disposed || surface is null || device is null) return;
        var info = WindowsDisplayState.ForWindow(WindowHandle);
        bool fp16 = surface.PixelFormat == CompositionFormat.R16G16B16A16Float;
        extended = fp16 && info.AdvancedColor;
        whiteScale = fp16 ? info.WhiteScale : 1;
        var shown = extended ? color : color.ClampToSdr();
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Microsoft.UI.Colors.Transparent);
        using var brush = new CanvasSolidColorBrush(device,Microsoft.UI.Colors.Transparent);
        if (fp16) brush.ColorHdr = new Vector4(shown.R*(float)whiteScale,shown.G*(float)whiteScale,shown.B*(float)whiteScale,1);
        else { var (r,g,b) = shown.ToSrgbBytes(); brush.Color = global::Windows.UI.Color.FromArgb(255,r,g,b); }
        float w=(float)surface.Size.Width,h=(float)surface.Size.Height;
        ds.FillEllipse(w/2,h/2,Math.Max(0,w/2-1),Math.Max(0,h/2-1),brush);
    }
    private void ReleaseResources()
    {
        ElementCompositionPreview.SetElementChildVisual(this,null);
        visual?.Dispose(); visual=null;
        surfaceBrush?.Dispose(); surfaceBrush=null;
        surface?.Dispose(); surface=null;
        graphics?.Dispose(); graphics=null;
        // GetSharedDevice is process-owned; disposing it would invalidate other swatches.
        device=null;
    }
    public void Dispose() { if(disposed) return; disposed=true; displayTimer.Stop(); ReleaseResources(); }
}
