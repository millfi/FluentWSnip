using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Core.Strings;

namespace WSnip.App.Capture;

/// <summary>One toolbar window for the entire capture operation, independent of frozen/live selection backgrounds.</summary>
internal sealed class CaptureToolbar : AppWindowBase
{
    private const uint DwmCloak = 13, DwmTransitionsForceDisabled = 3;
    private readonly SnipModeBar bar;
    private readonly DisplayArea display;
    private readonly TaskCompletionSource<bool> loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PixelRect? previousBounds;
    private bool layoutQueued;

    public CaptureToolbar(PointInt32 pointer, SnipMode mode, ThemeService theme)
    {
        Mode = mode;
        Title = AppStrings.OverlayTitle;
        SetDwmFlag(DwmCloak, true);
        SetDwmFlag(DwmTransitionsForceDisabled, true);
        MakeOverlay();
        display = DisplayArea.GetFromPoint(pointer, DisplayAreaFallback.Nearest);
        AppWindow.Move(new PointInt32(display.OuterBounds.X, display.OuterBounds.Y));
        bar = new SnipModeBar(mode, SelectMode, Cancel);
        Content = bar;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        theme.Attach(this, manageShellBackdrop: false);
        App.Platform!.Windows.SetExcludedFromCapture(Handle, true);
        bar.Loaded += (_, _) => { ResizeToContent(); loaded.TrySetResult(true); };
        bar.SizeChanged += (_, _) => QueueLayout();
        bar.ActualThemeChanged += (_, _) => QueueLayout();
        bar.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { Cancel(); e.Handled = true; }
        }), true);
        bar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(bar).Properties.IsRightButtonPressed) { Cancel(); e.Handled = true; }
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange || e.DidSizeChange || e.DidVisibilityChange) NotifyBoundsChanged();
        };
        Closed += (_, _) => { loaded.TrySetResult(false); Cancel(); };
    }

    public SnipMode Mode { get; private set; }
    public bool IsCancelled { get; private set; }
    public PixelRect PixelBounds => new(Position.X, Position.Y, PixelSize.Width, PixelSize.Height);
    public event Action<SnipMode>? ModeChanged;
    public event Action? CancelRequested;
    public event Action? BoundsChanged;

    public async Task<bool> ShowAsync()
    {
        AppWindow.Show(activateWindow: false);
        if (!await loaded.Task || IsCancelled) return false;
        ResizeToContent();
        bar.UpdateLayout();
        await Compositor.RequestCommitAsync();
        if (IsClosed || IsCancelled) return false;
        SetDwmFlag(DwmCloak, false);
        Activate();
        return true;
    }

    public void Cancel()
    {
        if (IsCancelled) return;
        IsCancelled = true;
        CancelRequested?.Invoke();
    }

    private void SelectMode(SnipMode mode)
    {
        if (IsCancelled || Mode == mode) return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    private void QueueLayout()
    {
        if (layoutQueued || IsClosed) return;
        layoutQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            layoutQueued = false;
            if (!IsClosed) ResizeToContent();
        });
    }

    private void ResizeToContent()
    {
        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = RenderScaling;
        int width = Math.Max(1, (int)Math.Ceiling(bar.DesiredSize.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(bar.DesiredSize.Height * scale));
        if (AppWindow.ClientSize.Width != width || AppWindow.ClientSize.Height != height)
            AppWindow.ResizeClient(new SizeInt32(width, height));
        var position = new PointInt32(display.OuterBounds.X + (display.OuterBounds.Width - PixelSize.Width) / 2,
            display.OuterBounds.Y + (int)Math.Round(24 * scale));
        if (Position.X != position.X || Position.Y != position.Y) AppWindow.Move(position);
        NotifyBoundsChanged();
    }

    private void NotifyBoundsChanged()
    {
        PixelRect bounds = PixelBounds;
        if (previousBounds == bounds) return;
        previousBounds = bounds;
        BoundsChanged?.Invoke();
    }
}

internal sealed class SnipModeBar : UserControl
{
    private readonly List<(SnipMode Mode, ToggleButton Button)> buttons = [];

    public SnipModeBar(SnipMode selected, Action<SnipMode> select, Action cancel)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var option in SnipModeOption.All)
        {
            var button = new ToggleButton
            {
                Content = new SymbolIcon(option.Mode switch
                {
                    SnipMode.Window => Symbol.OpenPane, SnipMode.Fullscreen => Symbol.FullScreen,
                    SnipMode.Freeform => Symbol.Edit, SnipMode.WholeWindow => Symbol.BrowsePhotos, _ => Symbol.SelectAll,
                }),
                IsChecked = option.Mode == selected,
            };
            ToolTipService.SetToolTip(button, string.Format(AppStrings.ModeDescription, option.Label, option.Description));
            AutomationProperties.SetName(button, option.Label);
            AutomationProperties.SetAutomationId(button, $"SnipMode{option.Mode}");
            button.Click += (_, _) =>
            {
                foreach (var item in buttons) item.Button.IsChecked = item.Mode == option.Mode;
                select(option.Mode);
            };
            buttons.Add((option.Mode, button));
            panel.Children.Add(button);
        }
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel) };
        AutomationProperties.SetName(close, AppStrings.Cancel);
        AutomationProperties.SetAutomationId(close, "CancelSnip");
        ToolTipService.SetToolTip(close, AppStrings.CancelTip);
        close.Click += (_, _) => cancel();
        panel.Children.Add(close);
        Content = new Border { Style = (Style)Application.Current.Resources["SnipModeBarStyle"], Child = panel };
    }
}
