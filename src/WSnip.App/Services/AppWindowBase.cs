using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WSnip.App.Services;

public enum WindowState { Normal, Minimized, Maximized }

/// <summary>Owns a WinUI window and its native lifetime, including tray-only operation.</summary>
public class AppWindowBase : Window
{
    public static List<AppWindowBase> Windows { get; } = [];
    public nint Handle { get; }
    public bool IsVisible => AppWindow.IsVisible;
    public bool IsActive { get; private set; }
    public bool IsClosed { get; private set; }
    public double RenderScaling => GetDpiForWindow(Handle) / 96.0;
    public event EventHandler? Opened;
    public event EventHandler? Closing;
    public event EventHandler? PositionChanged;


    public AppWindowBase()
    {
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Windows.Add(this);
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "wsnip.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Activated += (_, e) => IsActive = e.WindowActivationState != WindowActivationState.Deactivated;
        AppWindow.Closing += (_, _) => Closing?.Invoke(this, EventArgs.Empty);
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange) PositionChanged?.Invoke(this, EventArgs.Empty);

        };
        Closed += (_, _) =>
        {
            IsClosed = true;
            Windows.Remove(this);
            LightStudio.Logging.AppLog.Information("Lifetime", $"Closed '{Title}'; {Windows.Count} WinUI windows remain.");
        };
    }

    public void SetSize(double width, double height) => AppWindow.Resize(new SizeInt32((int)Math.Ceiling(width * RenderScaling), (int)Math.Ceiling(height * RenderScaling)));
    public void SetMinimumSize(double width, double height)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(width * RenderScaling);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(height * RenderScaling);
        }
    }
    public void Show(AppWindowBase? owner = null)
    {
        if (IsClosed) return;
        if (owner is not null) SetWindowLongPtr(Handle, -8, owner.Handle);
        bool first = !IsVisible;
        AppWindow.Show();
        Activate();
        if (first) Opened?.Invoke(this, EventArgs.Empty);
    }
    public void Hide() => AppWindow.Hide();
    public PointInt32 Position { get => AppWindow.Position; set => AppWindow.Move(value); }
    public SizeInt32 PixelSize { get => AppWindow.Size; set => AppWindow.Resize(value); }
    public WindowState WindowState
    {
        get => AppWindow.Presenter is OverlappedPresenter p ? p.State switch
        {
            OverlappedPresenterState.Maximized => WindowState.Maximized,
            OverlappedPresenterState.Minimized => WindowState.Minimized,
            _ => WindowState.Normal,
        } : WindowState.Normal;
        set
        {
            if (AppWindow.Presenter is not OverlappedPresenter p) return;
            if (value == WindowState.Maximized) p.Maximize();
            else if (value == WindowState.Minimized) p.Minimize();
            else p.Restore();
        }
    }
    public void MakeOverlay()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
    }
    public void CloseSafely() { if (!IsClosed) Close(); }
    protected void SetDwmFlag(uint attribute, bool enabled)
    {
        int value = enabled ? 1 : 0;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int)));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
