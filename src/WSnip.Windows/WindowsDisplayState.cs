using WSnip.Core.Capture;
using WSnip.Windows.Capture;
using WSnip.Windows.Interop;

namespace WSnip.Windows;

/// <summary>Uses the same display color information for capture and WinUI presentation.</summary>
public static class WindowsDisplayState
{
    private static readonly object Gate = new();
    private static List<DisplayInfo> displays = [];
    private static long refreshedAt = -1000;

    public static DisplayColorInfo ForWindow(nint hwnd)
    {
        nint monitor = Native.MonitorFromWindow(hwnd, 2);
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (now - refreshedAt >= 750)
            {
                displays = DisplayEnumerator.Enumerate();
                refreshedAt = now;
            }
            return displays.FirstOrDefault(display => display.Handle == monitor)?.Color ?? DisplayColorInfo.Sdr;
        }
    }
}
