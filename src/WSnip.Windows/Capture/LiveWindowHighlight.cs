using System.ComponentModel;
using System.Runtime.InteropServices;
using WSnip.Core.Imaging;
using WSnip.Windows.Interop;

namespace WSnip.Windows.Capture;

/// <summary>
/// Click-through, nonactivating layered windows dim the live desktop outside the hovered frame.
/// Window regions do the clipping: no desktop capture, full-screen pixel uploads, or UI-thread work.
/// All native windows and regions belong to the picker's message thread.
/// </summary>
internal sealed unsafe class LiveWindowHighlight : IDisposable
{
    private const uint ExtendedStyles = 0x08000000 | 0x00080000 | 0x80 | 0x20 | 0x08;
    private const uint WindowStyles = 0x80000000 | 0x08000000; // WS_POPUP | WS_DISABLED
    private const int RegionDifference = 4;
    private static readonly ushort BlackClass = Register("FluentWSnip.PickerShade", 4); // BLACK_BRUSH
    private static readonly ushort WhiteClass = Register("FluentWSnip.PickerBorder", 0); // WHITE_BRUSH
    private readonly List<Shade> shades = [];
    private readonly List<nint> controls = WindowEnumerator.OwnVisibleWindows();
    private PixelRect? previousSelection;
    private PixelRect[] previousControls = [];
    private bool initialized;

    public LiveWindowHighlight()
    {
        try
        {
            foreach (DisplayInfo display in DisplayEnumerator.Enumerate())
                shades.Add(new Shade(display));
        }
        catch { Dispose(); throw; }
    }

    public void Update(PixelRect? selection)
    {
        var exclusions = new List<PixelRect>(controls.Count);
        foreach (nint handle in controls)
        {
            Rect rect;
            if (Native.IsWindowVisible(handle) && Native.GetWindowRect(handle, &rect))
                exclusions.Add(PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom));
        }
        if (initialized && previousSelection == selection && previousControls.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(exclusions)))
            return;
        previousSelection = selection;
        previousControls = exclusions.ToArray();
        foreach (Shade shade in shades) shade.Update(selection, exclusions);
        if (!initialized)
        {
            foreach (Shade shade in shades) shade.Show();
            initialized = true;
        }
    }

    public void Dispose()
    {
        foreach (Shade shade in shades) shade.Dispose();
        shades.Clear();
    }

    private sealed class Shade : IDisposable
    {
        private readonly DisplayInfo display;
        private nint dim, shadow, border;

        public Shade(DisplayInfo display)
        {
            this.display = display;
            try
            {
                dim = Create(BlackClass, display.Bounds, 144);
                shadow = Create(BlackClass, display.Bounds, 160);
                border = Create(WhiteClass, display.Bounds, 255);
            }
            catch { Dispose(); throw; }
        }

        public void Update(PixelRect? selection, IReadOnlyList<PixelRect> exclusions)
        {
            PixelRect screen = display.Bounds;
            nint mask = Rectangle(new PixelRect(0, 0, screen.Width, screen.Height));
            try
            {
                if (selection is { } rect) Subtract(mask, Local(rect));
                Apply(dim, ref mask);
            }
            finally { if (mask != 0) Native.DeleteObject(mask); }
            Stroke(shadow, Math.Max(1, (int)Math.Round(3.5 * display.Scaling)));
            Stroke(border, Math.Max(1, (int)Math.Round(1.5 * display.Scaling)));

            PixelRect Local(PixelRect rect) => rect.Offset(-screen.X, -screen.Y);
            void Stroke(nint window, int width)
            {
                nint region = Rectangle(default);
                try
                {
                    if (selection is { } rect)
                    {
                        Native.DeleteObject(region);
                        region = 0;
                        rect = Local(rect);
                        int outside = (width + 1) / 2, inside = width / 2;
                        region = Rectangle(PixelRect.FromEdges(rect.X - outside, rect.Y - outside, rect.Right + outside, rect.Bottom + outside));
                        Subtract(region, PixelRect.FromEdges(rect.X + inside, rect.Y + inside, rect.Right - inside, rect.Bottom - inside));
                    }
                    Apply(window, ref region);
                }
                finally { if (region != 0) Native.DeleteObject(region); }
            }
            void Apply(nint window, ref nint region)
            {
                foreach (PixelRect excluded in exclusions) Subtract(region, Local(excluded));
                if (Native.SetWindowRgn(window, region, true) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                region = 0; // SetWindowRgn transfers ownership to the system.
                Native.UpdateWindow(window);
            }
        }

        public void Show()
        {
            Native.ShowWindow(dim, 4); // SW_SHOWNOACTIVATE; never steal the picker toolbar's focus.
            Native.ShowWindow(shadow, 4);
            Native.ShowWindow(border, 4);
        }

        public void Dispose()
        {
            if (border != 0) Native.DestroyWindow(border);
            if (shadow != 0) Native.DestroyWindow(shadow);
            if (dim != 0) Native.DestroyWindow(dim);
            border = shadow = dim = 0;
        }
    }

    private static nint Create(ushort windowClass, PixelRect bounds, byte alpha)
    {
        nint window = Native.CreateWindowEx(ExtendedStyles, (char*)(nuint)windowClass, null, WindowStyles,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, Native.GetModuleHandle(null), 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!Native.SetLayeredWindowAttributes(window, 0, alpha, 2)) // LWA_ALPHA
        {
            int error = Marshal.GetLastPInvokeError();
            Native.DestroyWindow(window);
            throw new Win32Exception(error);
        }
        return window;
    }

    private static nint Rectangle(PixelRect rect)
    {
        nint region = Native.CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
        return region != 0 ? region : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static void Subtract(nint region, PixelRect rectangle)
    {
        nint hole = Rectangle(rectangle);
        try
        {
            if (Native.CombineRgn(region, region, hole, RegionDifference) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { Native.DeleteObject(hole); }
    }

    private static ushort Register(string name, int brush)
    {
        fixed (char* className = name)
        {
            WindowClassEx definition = new()
            {
                Size = (uint)sizeof(WindowClassEx), WindowProcedure = &WindowProcedure,
                Instance = Native.GetModuleHandle(null), Background = Native.GetStockObject(brush), ClassName = className,
            };
            ushort atom = Native.RegisterClassEx(&definition);
            return atom != 0 ? atom : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        message == 0x0084 ? -1 : Native.DefWindowProc(window, message, wParam, lParam); // HTTRANSPARENT
}
