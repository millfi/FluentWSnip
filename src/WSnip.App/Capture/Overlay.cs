using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Foundation;
using Windows.Graphics;
using SkiaSharp;
using WSnip.App.Rendering;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Core.Strings;
using PixelPoint = Windows.Graphics.PointInt32;

namespace WSnip.App.Capture;

/// <summary>The region the user picked, in physical virtual-desktop pixels.</summary>
public sealed record SnipSelection(SnipMode Mode, PixelRect Region, IReadOnlyList<(double X, double Y)>? Outline);

/// <summary>
/// The frozen-screen overlay: one borderless topmost window per display showing what was captured,
/// dimmed outside the selection, with the snip mode bar on the display under the pointer. Where the
/// compositor places windows, a single full-screen window shows the display it opens on.
/// </summary>
public sealed class OverlaySession : IDisposable
{
    private readonly ScreenSnapshot snapshot;
    private readonly bool placeWindows;
    private readonly List<OverlayWindow> windows = [];
    private readonly TaskCompletionSource<SnipSelection?> result = new();
    private PixelPoint? anchor;
    private PixelRect? selection;
    private List<(double X, double Y)>? outline;
    private bool finished;

    /// <param name="placeWindows">Whether windows can be placed on each display, rather than by the compositor.</param>
    public OverlaySession(ScreenSnapshot snapshot, SnipMode mode, bool placeWindows)
    {
        this.snapshot = snapshot;
        this.placeWindows = placeWindows;
        Mode = mode;
    }

    public event EventHandler? Changed;

    public SnipMode Mode { get; private set; }

    public ScreenSnapshot Snapshot => snapshot;

    /// <summary>The rectangle being dragged or the hovered window or display.</summary>
    public PixelRect? Selection => selection;

    public IReadOnlyList<(double X, double Y)>? Outline => outline;

    public bool IsDragging => anchor is not null;

    public Task<SnipSelection?> ShowAsync(PixelPoint pointer)
    {
        MonitorCapture barMonitor = snapshot.MonitorAt(pointer.X, pointer.Y)
            ?? snapshot.Monitors.FirstOrDefault(m => m.IsPrimary) ?? snapshot.Monitors[0];
        foreach (MonitorCapture monitor in snapshot.Monitors)
        {
            var window = new OverlayWindow(this, monitor, monitor == barMonitor);
            windows.Add(window);
            window.Show();
        }

        windows.FirstOrDefault(w => w.Monitor == barMonitor)?.Activate();
        UpdateHover(pointer);
        return result.Task;
    }

    /// <summary>Redraws the windows after one of them changed what it shows.</summary>
    internal void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    public void SetMode(SnipMode mode)
    {
        Mode = mode;
        anchor = null;
        selection = null;
        outline = null;
        Changed?.Invoke(this, EventArgs.Empty);

        // A whole window is picked on the live screen, so the frozen one goes away.
        if (mode == SnipMode.WholeWindow)
            Finish(null);
    }

    public void Cancel() => Finish(null);

    public void Dispose() => Finish(null);

    public void PointerPressed(PixelPoint point)
    {
        switch (Mode)
        {
            case SnipMode.Rectangle:
                anchor = point;
                selection = new PixelRect(point.X, point.Y, 0, 0);
                break;
            case SnipMode.Freeform:
                anchor = point;
                outline = [(point.X + 0.5, point.Y + 0.5)];
                selection = null;
                break;
            case SnipMode.Window:
            case SnipMode.Fullscreen:
                UpdateHover(point);
                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PointerMoved(PixelPoint point)
    {
        if (anchor is not { } start)
        {
            UpdateHover(point);
            return;
        }

        if (Mode == SnipMode.Rectangle)
        {
            selection = PixelRect.FromEdges(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y),
                Math.Max(start.X, point.X), Math.Max(start.Y, point.Y));
        }
        else if (Mode == SnipMode.Freeform && outline is not null)
        {
            (double lastX, double lastY) = outline[^1];
            if (Math.Abs(point.X + 0.5 - lastX) + Math.Abs(point.Y + 0.5 - lastY) >= 2)
                outline.Add((point.X + 0.5, point.Y + 0.5));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PointerReleased(PixelPoint point)
    {
        switch (Mode)
        {
            case SnipMode.Rectangle when anchor is not null:
                PointerMoved(point);
                anchor = null;
                if (selection is { Width: >= 2, Height: >= 2 } rect)
                    Finish(new SnipSelection(Mode, rect, null));
                else
                    selection = null;
                break;
            case SnipMode.Freeform when anchor is not null && outline is not null:
                anchor = null;
                PixelRect bounds = OutlineBounds(outline);
                if (outline.Count >= 3 && bounds.Width >= 4 && bounds.Height >= 4)
                    Finish(new SnipSelection(Mode, bounds, outline));
                else
                    outline = null;
                break;
            case SnipMode.Window:
            case SnipMode.Fullscreen:
                UpdateHover(point);
                if (selection is { } hovered)
                    Finish(new SnipSelection(Mode, hovered, null));
                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHover(PixelPoint point)
    {
        PixelRect? hovered = Mode switch
        {
            SnipMode.Window => snapshot.WindowAt(point.X, point.Y)?.Bounds ?? snapshot.MonitorAt(point.X, point.Y)?.Bounds,
            SnipMode.Fullscreen => snapshot.MonitorAt(point.X, point.Y)?.Bounds,
            _ => null,
        };
        if (hovered != selection)
        {
            selection = hovered;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Finish(SnipSelection? selected)
    {
        if (finished)
            return;
        finished = true;
        foreach (OverlayWindow window in windows)
            window.CloseSafely();
        windows.Clear();
        result.TrySetResult(selected);
    }

    private static PixelRect OutlineBounds(IReadOnlyList<(double X, double Y)> points)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        double right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        return PixelRect.FromEdges((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
    }
}


internal sealed class OverlayWindow : AppWindowBase
{
    private readonly OverlaySession session;
    private readonly OverlaySurface surface;
    private readonly DispatcherTimer follow = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool pressed;
    public MonitorCapture Monitor { get; }
    public OverlayWindow(OverlaySession session, MonitorCapture monitor, bool showBar)
    {
        this.session = session; Monitor = monitor;
        Title = AppStrings.OverlayTitle;
        MakeOverlay();
        AppWindow.MoveAndResize(new RectInt32(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height));
        surface = new OverlaySurface(session, monitor) { WindowHandle = Handle, FixedDisplay = monitor.Color };
        surface.RenderingFailed += (_, e) => { session.Cancel(); MessageDialog.Show(null, "WSnip", e.Message); };
        var root = new Grid(); root.Children.Add(surface);
        if (showBar)
            root.Children.Add(new SnipModeBar(session.Mode, session.SetMode, session.Cancel)
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0,24,0,0),
            });
        Content = root;
        surface.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(surface);
            if (p.Properties.IsRightButtonPressed) { session.Cancel(); return; }
            if (!p.Properties.IsLeftButtonPressed) return;
            pressed = true;
            surface.CapturePointer(e.Pointer);
            session.PointerPressed(Pointer(e.GetCurrentPoint(surface).Position));
            follow.Start(); e.Handled = true;
        };
        surface.PointerMoved += (_, e) => session.PointerMoved(Pointer(e.GetCurrentPoint(surface).Position));
        surface.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false; follow.Stop();
            var point = Pointer(e.GetCurrentPoint(surface).Position);
            surface.ReleasePointerCapture(e.Pointer);
            session.PointerReleased(point); e.Handled = true;
        };
        surface.PointerCanceled += (_, _) => session.Cancel();
        surface.PointerCaptureLost += (_, _) => { if (pressed) { pressed = false; follow.Stop(); session.Cancel(); } };
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (e.Key == VirtualKey.Escape) { session.Cancel(); e.Handled = true; } }), true);
        follow.Tick += (_, _) => { if (App.Platform!.Windows.TryGetCursorPosition(out int x,out int y)) session.PointerMoved(new PixelPoint(x,y)); };
        session.Changed += SessionChanged;
        Opened += (_, _) => AppWindow.MoveAndResize(new RectInt32(monitor.Bounds.X,monitor.Bounds.Y,monitor.Bounds.Width,monitor.Bounds.Height));
        Closed += (_, _) => { follow.Stop(); session.Changed -= SessionChanged; surface.Dispose(); session.Cancel(); };
    }
    private PixelPoint Pointer(Point local)
    {
        if (App.Platform!.Windows.TryGetCursorPosition(out int x,out int y)) return new PixelPoint(x,y);
        return new PixelPoint(Monitor.Bounds.X + (int)(local.X * RenderScaling), Monitor.Bounds.Y + (int)(local.Y * RenderScaling));
    }
    private void SessionChanged(object? sender, EventArgs e) => surface.InvalidateVisual();
}

internal sealed class OverlaySurface : HdrSurface
{
    private readonly OverlaySession session;
    private readonly MonitorCapture monitor;
    private readonly SharedImage image;
    public OverlaySurface(OverlaySession session, MonitorCapture monitor)
    {
        this.session = session; this.monitor = monitor;
        image = SharedImage.FromLinear(monitor.Image,monitor.Color.WhiteScale);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }
    private SKPoint Local(double x,double y) => new((float)((x-monitor.Bounds.X)/RasterScale),(float)((y-monitor.Bounds.Y)/RasterScale));
    protected override void Draw(SKCanvas canvas)
    {
        var area = new SKRect(0,0,(float)ActualWidth,(float)ActualHeight);
        SnipDrawing.Draw(canvas,image,area,[],false,Surface.ReferenceWhiteScale,false);
        using var builder = new SKPathBuilder();
        if (session.Mode == SnipMode.Freeform && session.Outline is { Count: >= 2 } outline)
        {
            builder.MoveTo(Local(outline[0].X,outline[0].Y));
            foreach (var point in outline.Skip(1)) builder.LineTo(Local(point.X,point.Y));
            builder.Close();
        }
        else if (session.Selection is { Width: > 0, Height: > 0 } rect)
        {
            var a=Local(rect.X,rect.Y); var b=Local(rect.Right,rect.Bottom);
            builder.AddRect(new SKRect(a.X,a.Y,b.X,b.Y));
        }
        using var hole = builder.Detach();
        using var dim = new SKPaint { Color = new SKColor(0,0,0,144) };
        canvas.Save(); canvas.ClipPath(hole,SKClipOperation.Difference); canvas.DrawRect(area,dim); canvas.Restore();
        using var shadow = new SKPaint { Color = new SKColor(0,0,0,160),Style = SKPaintStyle.Stroke,StrokeWidth = 3.5f,IsAntialias = true };
        canvas.DrawPath(hole,shadow);
        using var linear = SKColorSpace.CreateSrgbLinear();
        using var border = new SKPaint { Style = SKPaintStyle.Stroke,StrokeWidth = 1.5f,IsAntialias = true };
        border.SetColor(new SKColorF(1,1,1,1),linear);
        canvas.DrawPath(hole,border);
    }
    public override void Dispose() { image.Release(); base.Dispose(); }
}

internal sealed class SnipModeBar : UserControl
{
    public SnipModeBar(SnipMode selected, Action<SnipMode> select, Action cancel)
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        CornerRadius = new CornerRadius(8); Padding = new Thickness(8);
        RequestedTheme = ElementTheme.Dark;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var buttons = new List<ToggleButton>();
        foreach (var option in SnipModeOption.All)
        {
            var button = new ToggleButton { Content = new SymbolIcon(option.Mode switch { SnipMode.Window => Symbol.OpenPane, SnipMode.Fullscreen => Symbol.FullScreen, SnipMode.Freeform => Symbol.Edit, SnipMode.WholeWindow => Symbol.BrowsePhotos, _ => Symbol.SelectAll }), IsChecked = option.Mode == selected };
            ToolTipService.SetToolTip(button,string.Format(AppStrings.ModeDescription,option.Label,option.Description));
            AutomationProperties.SetName(button,option.Label);
            AutomationProperties.SetAutomationId(button,$"SnipMode{option.Mode}");
            button.Click += (_, _) => { foreach(var other in buttons) other.IsChecked = other == button; select(option.Mode); };
            buttons.Add(button); panel.Children.Add(button);
        }
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel) };
        AutomationProperties.SetName(close,AppStrings.Cancel);
        AutomationProperties.SetAutomationId(close,"CancelSnip");
        ToolTipService.SetToolTip(close,AppStrings.CancelTip);
        close.Click += (_, _) => cancel(); panel.Children.Add(close); Content = panel;
    }
}

internal sealed class WholeWindowToolbar : AppWindowBase
{
    public SnipMode? RequestedMode { get; private set; }
    public WholeWindowToolbar(PixelPoint pointer, bool placeWindow, Action cancel)
    {
        Title = AppStrings.OverlayTitle; MakeOverlay();
        Content = new SnipModeBar(SnipMode.WholeWindow, mode => { if(mode != SnipMode.WholeWindow) { RequestedMode = mode; cancel(); } }, cancel);
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(pointer,Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        Position = new PointInt32(display.OuterBounds.X + display.OuterBounds.Width / 2 - 180,display.OuterBounds.Y + 24);
        SetSize(380,64);
        Closed += (_, _) => cancel();
    }
}
