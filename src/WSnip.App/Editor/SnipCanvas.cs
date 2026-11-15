using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;

using Windows.Foundation;
using WSnip.App.Rendering;
using WSnip.App.Services;
using WSnip.Core.Editing;
using WSnip.Core.Imaging;

namespace WSnip.App.Editor;

public enum EditorTool { Select, Pen, Highlighter, Eraser, Crop }
public readonly record struct PixelHover(int X, int Y, Point Position);

/// <summary>WinUI pointer input and a full precision image viewport, in physical-pixel zoom units.</summary>
public sealed class SnipCanvas : HdrSurface
{
    private EditorDocument? document;
    private EditorTool tool;
    private bool showHdr = true, picking, fit = true;
    private bool? hdrOverride;
    private double zoom = 1, bottomInset;
    private ToneMapSettings toneMap = ToneMapSettings.Default;
    private Vector2 offset, panOrigin, start;
    private uint? activePointer;
    private bool panning;
    private List<Vector2>? points;
    private HashSet<AnnotationStroke>? erased;
    private PixelRect? crop;
    private CropDrag cropDrag;
    private Vector2 cropStart;
    private PixelRect cropOrigin;
    private readonly DispatcherTimer zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch zoomClock = new();
    private double fromZoom, targetZoom;
    private Vector2 zoomImageAnchor, zoomViewAnchor;
    private const float HandleSize = 8;
    public event EventHandler? ViewChanged;
    public event EventHandler? CropChanged;
    public event EventHandler<PixelHover?>? HoverChanged;
    public event EventHandler<PixelHover>? PixelPicked;

    public SnipCanvas()
    {
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += (_, _) => CancelInput();
        PointerCaptureLost += (_, e) => { if (activePointer == e.Pointer.PointerId) CancelInput(); };
        PointerExited += (_, _) => SetHover(null);
        PointerWheelChanged += Wheel;
        DoubleTapped += (_, e) => { if (tool == EditorTool.Select && !picking) { AnimateZoom(IsFit ? 1 : FitZoom(), V(e.GetPosition(this))); e.Handled = true; } };
        SizeChanged += (_, _) => { ClampOffset(); ChangedView(); };
        Unloaded += (_, _) => { zoomTimer.Stop(); CancelInput(); };
        SurfaceChanged += (_, _) => InvalidateVisual();
        zoomTimer.Tick += (_, _) =>
        {
            double t = Math.Clamp(zoomClock.Elapsed.TotalMilliseconds / 160, 0, 1);
            ApplyZoom(fromZoom + (targetZoom - fromZoom) * (1 - Math.Pow(1 - t, 3)), zoomImageAnchor, zoomViewAnchor);
            if (t >= 1) zoomTimer.Stop();
        };
        ManipulationMode = ManipulationModes.Scale | ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        ManipulationDelta += (_, e) =>
        {
            if (tool != EditorTool.Select || picking || document is null) return;
            zoomTimer.Stop();
            ApplyZoom(Zoom * e.Delta.Scale, ToImage(V(e.Position)), V(e.Position));
            offset += V(e.Delta.Translation);
            ClampOffset(); ChangedView(); e.Handled = true;
        };
    }
    public EditorDocument? Document
    {
        get => document;
        set
        {
            if (ReferenceEquals(document, value)) return;
            zoomTimer.Stop(); CancelInput();
            if (document is not null) { document.Changed -= DocumentChanged; document.RenditionChanged -= RenditionChanged; }
            document = value;
            if (document is not null) { document.Changed += DocumentChanged; document.RenditionChanged += RenditionChanged; }
            fit = true; offset = default; crop = null; SetHover(null); ChangedView();
            if (value is null) ReleaseBuffers();
        }
    }
    public EditorTool Tool
    {
        get => tool;
        set
        {
            if (tool == value) return;
            CancelInput(); tool = value;
            ManipulationMode = value == EditorTool.Select ? ManipulationModes.Scale | ManipulationModes.TranslateX | ManipulationModes.TranslateY : ManipulationModes.None;
            if (value == EditorTool.Crop) ResetCrop();
            else { crop = null; CropChanged?.Invoke(this, EventArgs.Empty); }
            SetCursor(); InvalidateVisual();
        }
    }
    public ScRgb StrokeColor { get; set; } = new(1, 0, 0);
    public double StrokeSize { get; set; } = 4;
    public bool ShowHdr { get => showHdr; set { showHdr = value; InvalidateVisual(); } }
    public bool? ShowHdrOverride { get => hdrOverride; set { hdrOverride = value; InvalidateVisual(); } }
    public ToneMapSettings DefaultToneMap { get => toneMap; set { toneMap = value; InvalidateVisual(); } }
    public bool IsPickingColor { get => picking; set { picking = value; SetCursor(); } }
    public double BottomInset { get => bottomInset; set { if (Math.Abs(bottomInset - value) < 0.1) return; bottomInset = value; ClampOffset(); ChangedView(); } }
    public double Zoom => fit ? FitZoom() : zoom;
    public bool IsFit => fit;
    public PixelRect? CropRegion => crop;
    public PixelHover? Hover { get; private set; }
    public void ZoomBy(double factor) => AnimateZoom((zoomTimer.IsEnabled ? targetZoom : Zoom) * factor, Center);
    public void ZoomToFit() => AnimateZoom(FitZoom(), Center);
    public void ZoomToActualSize() => AnimateZoom(1, Center);
    public void ResetCrop() { crop = document is null ? null : new PixelRect(0, 0, document.Width, document.Height); CropChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual(); }
    private void DocumentChanged(object? sender, EventArgs e) { if (tool == EditorTool.Crop) ResetCrop(); ClampOffset(); ChangedView(); }
    private void RenditionChanged(object? sender, EventArgs e) => InvalidateVisual();
    private void ChangedView() { SetCursor(); InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty); }
    private void SetCursor() => ProtectedCursor = InputSystemCursor.Create(picking || tool is EditorTool.Pen or EditorTool.Highlighter or EditorTool.Crop ? InputSystemCursorShape.Cross : tool == EditorTool.Eraser ? InputSystemCursorShape.Hand : CanPan ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow);
    protected override void Draw(SKCanvas canvas)
    {
        using var srgb = SKColorSpace.CreateSrgb();
        using var background = new SKPaint();
        float shade = ActualTheme == ElementTheme.Dark ? 0.07f : 0.91f;
        background.SetColor(new SKColorF(shade, shade, shade, 1), srgb);
        canvas.DrawRect(0, 0, (float)ActualWidth, (float)ActualHeight, background);
        if (document is null) return;
        bool hdr = Surface.ExtendedRange && (!document.Current.Statistics.HasHdr || (hdrOverride ?? showHdr));
        var image = hdr ? document.GetHdrImage((float)Surface.Headroom) : document.GetSdrImage(toneMap);
        IReadOnlyList<AnnotationStroke> strokes = document.Current.Strokes;
        if (erased?.Count > 0) strokes = strokes.Where(s => !erased.Contains(s)).ToArray();
        if (points?.Count > 0) strokes = [.. strokes, CreateStroke(points)];
        var rect = ImageRect();
        SnipDrawing.Draw(canvas, image, rect, strokes, Zoom < 1.5, Surface.ReferenceWhiteScale, document.Current.Statistics.VisiblePixels < (long)document.Width * document.Height);
        if (tool == EditorTool.Crop && crop is { } region) DrawCrop(canvas, rect, region);
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (document is null || activePointer is not null) return;
        var p = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && tool == EditorTool.Select && !picking) return;
        if (!p.Properties.IsLeftButtonPressed && !p.Properties.IsMiddleButtonPressed) return;
        zoomTimer.Stop();
        var position = V(p.Position);
        if (picking)
        {
            if (PixelAt(position) is { } pixel) PixelPicked?.Invoke(this, pixel);
            e.Handled = true; return;
        }
        activePointer = e.Pointer.PointerId; start = position;
        if (p.Properties.IsMiddleButtonPressed || tool == EditorTool.Select) { panning = true; panOrigin = offset; }
        else if (tool is EditorTool.Pen or EditorTool.Highlighter) points = [ToImage(position)];
        else if (tool == EditorTool.Eraser) { erased = []; EraseAt(position); }
        else if (tool == EditorTool.Crop && crop is { } region)
        {
            cropDrag = HitCrop(region, position); cropStart = position; cropOrigin = region;
            if (cropDrag == CropDrag.None)
            {
                cropDrag = CropDrag.New;
                var imagePoint = ToImage(position);
                cropOrigin = new PixelRect((int)Math.Round(imagePoint.X), (int)Math.Round(imagePoint.Y), 0, 0);
            }
        }
        CapturePointer(e.Pointer); e.Handled = true; InvalidateVisual();
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        var position = V(e.GetCurrentPoint(this).Position);
        SetHover(PixelAt(position));
        if (activePointer != e.Pointer.PointerId) return;
        if (panning) { offset = panOrigin + position - start; ClampOffset(); ChangedView(); }
        else if (points is not null)
        {
            // Preserve pen samples delivered between UI frames.
            foreach (var p in e.GetIntermediatePoints(this).Reverse())
            {
                var point = ToImage(V(p.Position));
                if (Vector2.Distance(point, points[^1]) * ImageScale > 0.5f) points.Add(point);
            }
            InvalidateVisual();
        }
        else if (erased is not null) EraseAt(position);
        else if (cropDrag != CropDrag.None && document is not null) { crop = DragCrop(document, position); CropChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual(); }
        e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (activePointer != e.Pointer.PointerId) return;
        var stroke = points; var removed = erased;
        points = null; erased = null; panning = false; activePointer = null; cropDrag = CropDrag.None;
        ReleasePointerCapture(e.Pointer);
        if (stroke is not null) document?.AddStroke(CreateStroke(stroke));
        else if (removed is not null) document?.Erase(removed);
        if (crop is { Width: < 2 } or { Height: < 2 }) ResetCrop();
        e.Handled = true; InvalidateVisual();
    }
    private void CancelInput() { activePointer = null; panning = false; points = null; erased = null; cropDrag = CropDrag.None; ReleasePointerCaptures(); InvalidateVisual(); }
    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (document is null) return;
        var p = e.GetCurrentPoint(this);
        if (p.Properties.IsHorizontalMouseWheel)
        {
            offset.X += p.Properties.MouseWheelDelta / 120f * 50; ClampOffset(); ChangedView();
        }
        else AnimateZoom((zoomTimer.IsEnabled ? targetZoom : Zoom) * Math.Pow(1.25, p.Properties.MouseWheelDelta / 120.0), V(p.Position));
        SetHover(PixelAt(V(p.Position))); e.Handled = true;
    }
    private void AnimateZoom(double value, Vector2 anchor)
    {
        if (document is null) return;
        fromZoom = Zoom; targetZoom = Math.Clamp(value, FitZoom(), 32); zoomImageAnchor = ToImage(anchor); zoomViewAnchor = anchor;
        zoomClock.Restart(); zoomTimer.Start();
    }
    private void ApplyZoom(double value, Vector2 imagePoint, Vector2 anchor)
    {
        if (document is null) return;
        double minimum = FitZoom(); zoom = Math.Clamp(value, minimum, 32); fit = Math.Abs(zoom - minimum) < 1e-6;
        float scale = ImageScale;
        offset = anchor - imagePoint * scale + new Vector2(document.Width, document.Height) * scale / 2 - Center;
        ClampOffset(); ChangedView();
    }
    private AnnotationStroke CreateStroke(IReadOnlyList<Vector2> vertices) => new(tool == EditorTool.Highlighter ? AnnotationTool.Highlighter : AnnotationTool.Pen, StrokeColor, MathF.Max(1, (float)StrokeSize / ImageScale), vertices.ToArray());
    private void EraseAt(Vector2 position)
    {
        foreach (var stroke in document!.Current.Strokes)
            if (stroke.HitTest(ToImage(position), 6 / ImageScale)) erased!.Add(stroke);
        InvalidateVisual();
    }
    private void SetHover(PixelHover? value) { if (Hover == value) return; Hover = value; HoverChanged?.Invoke(this, value); }
    private PixelHover? PixelAt(Vector2 view)
    {
        if (document is null) return null;
        var point = ToImage(view); int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        return x >= 0 && y >= 0 && x < document.Width && y < document.Height ? new PixelHover(x, y, new Point(view.X, view.Y)) : null;
    }
    private static Vector2 V(Point point) => new((float)point.X, (float)point.Y);
    private float ImageScale => (float)(Zoom / RasterScale);
    private float ViewHeight => (float)Math.Max(1, ActualHeight - bottomInset);
    private Vector2 Center => new((float)ActualWidth / 2, ViewHeight / 2);
    private bool CanPan => document is not null && (document.Width * ImageScale > ActualWidth + 1 || document.Height * ImageScale > ViewHeight + 1);
    private double FitZoom() => document is null || ActualWidth <= 0 ? 1 : Math.Min(1, Math.Max(0.001, Math.Min(Math.Max(1, ActualWidth - 48) * RasterScale / document.Width, Math.Max(1, ViewHeight - 48) * RasterScale / document.Height)));
    private SKRect ImageRect()
    {
        if (document is null) return default;
        var half = new Vector2(document.Width, document.Height) * ImageScale / 2; var center = Center + offset;
        return new SKRect(center.X - half.X, center.Y - half.Y, center.X + half.X, center.Y + half.Y);
    }
    private Vector2 ToImage(Vector2 point) { var rect = ImageRect(); return (point - new Vector2(rect.Left, rect.Top)) / ImageScale; }
    private Vector2 ToView(double x, double y) { var rect = ImageRect(); return new Vector2(rect.Left + (float)x * ImageScale, rect.Top + (float)y * ImageScale); }
    private void ClampOffset()
    {
        if (fit) { offset = default; return; }
        if (document is null) return;
        float x = Math.Max(0, (document.Width * ImageScale - (float)ActualWidth) / 2), y = Math.Max(0, (document.Height * ImageScale - ViewHeight) / 2);
        offset = Vector2.Clamp(offset, new Vector2(-x, -y), new Vector2(x, y));
    }
    private enum CropDrag { None, New, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
    private CropDrag HitCrop(PixelRect region, Vector2 point)
    {
        var a = ToView(region.X, region.Y); var b = ToView(region.Right, region.Bottom);
        if (point.X < a.X - HandleSize || point.X > b.X + HandleSize || point.Y < a.Y - HandleSize || point.Y > b.Y + HandleSize) return CropDrag.None;
        bool l = Math.Abs(point.X - a.X) <= HandleSize, r = Math.Abs(point.X - b.X) <= HandleSize, t = Math.Abs(point.Y - a.Y) <= HandleSize, d = Math.Abs(point.Y - b.Y) <= HandleSize;
        return (l,t,r,d) switch
        {
            (true,true,_,_) => CropDrag.TopLeft, (_,true,true,_) => CropDrag.TopRight,
            (true,_,_,true) => CropDrag.BottomLeft, (_,_,true,true) => CropDrag.BottomRight,
            (true,_,_,_) => CropDrag.Left, (_,true,_,_) => CropDrag.Top, (_,_,true,_) => CropDrag.Right, (_,_,_,true) => CropDrag.Bottom,
            _ when document is not null && region == new PixelRect(0,0,document.Width,document.Height) => CropDrag.None, _ => CropDrag.Move,
        };
    }
    private PixelRect DragCrop(EditorDocument doc, Vector2 position)
    {
        int dx = (int)Math.Round((position.X - cropStart.X) / ImageScale), dy = (int)Math.Round((position.Y - cropStart.Y) / ImageScale);
        int l = cropOrigin.X, t = cropOrigin.Y, r = cropOrigin.Right, b = cropOrigin.Bottom;
        if (cropDrag == CropDrag.Move) return cropOrigin.Offset(Math.Clamp(dx, -l, doc.Width - r), Math.Clamp(dy, -t, doc.Height - b));
        if (cropDrag == CropDrag.New) { r = l + dx; b = t + dy; }
        else
        {
            if (cropDrag is CropDrag.Left or CropDrag.TopLeft or CropDrag.BottomLeft) l += dx;
            if (cropDrag is CropDrag.Right or CropDrag.TopRight or CropDrag.BottomRight) r += dx;
            if (cropDrag is CropDrag.Top or CropDrag.TopLeft or CropDrag.TopRight) t += dy;
            if (cropDrag is CropDrag.Bottom or CropDrag.BottomLeft or CropDrag.BottomRight) b += dy;
        }
        return PixelRect.FromEdges(Math.Min(l,r),Math.Min(t,b),Math.Max(l,r),Math.Max(t,b)).Intersect(new PixelRect(0,0,doc.Width,doc.Height));
    }
    private void DrawCrop(SKCanvas canvas, SKRect image, PixelRect region)
    {
        var a = ToView(region.X,region.Y); var b = ToView(region.Right,region.Bottom); var rect = new SKRect(a.X,a.Y,b.X,b.Y);
        canvas.Save(); canvas.ClipRect(image); canvas.ClipRect(rect, SKClipOperation.Difference);
        using (var dim = new SKPaint { Color = new SKColor(0,0,0,128) }) canvas.DrawRect(image,dim);
        canvas.Restore();
        using var line = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        canvas.DrawRect(rect,line);
        using var handle = new SKPaint { Color = SKColors.White };
        foreach (var p in new[] { a,b,new Vector2(a.X,b.Y),new Vector2(b.X,a.Y),new Vector2((a.X+b.X)/2,a.Y),new Vector2((a.X+b.X)/2,b.Y),new Vector2(a.X,(a.Y+b.Y)/2),new Vector2(b.X,(a.Y+b.Y)/2) })
            canvas.DrawRect(p.X-4,p.Y-4,8,8,handle);
    }
    public override void Dispose() { zoomTimer.Stop(); Document = null; base.Dispose(); }
}
