using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.ComponentModel;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Windows.Interop;

namespace WSnip.Windows;

/// <summary>
/// DWM window integration: the Mica Alt backdrop, the caption theme and the display affinity
/// that keeps the app's own windows out of its captures.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsIntegrationService : IWindowIntegrationService
{
    private const int NoBackdrop = 1;
    private const int TabbedBackdrop = 4;
    private const uint AffinityNone = 0;
    private const uint AffinityExcludeFromCapture = 0x11;

    public bool IsMicaSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public bool CanExcludeFromCapture => true;

    public bool CanPlaceWindows => true;

    public void SetMica(nint windowHandle, bool enabled, bool darkTheme)
    {
        if (windowHandle == 0 || !IsMicaSupported)
            return;
        SetDarkFrame(windowHandle, darkTheme);
        int backdrop = enabled ? TabbedBackdrop : NoBackdrop;
        Native.DwmSetWindowAttribute(windowHandle, Native.DwmSystemBackdropType, &backdrop, sizeof(int));
    }

    public void SetDarkFrame(nint windowHandle, bool darkTheme)
    {
        if (windowHandle == 0)
            return;
        int dark = darkTheme ? 1 : 0;
        Native.DwmSetWindowAttribute(windowHandle, Native.DwmUseImmersiveDarkMode, &dark, sizeof(int));
    }

    public void SetExcludedFromCapture(nint windowHandle, bool excluded)
    {
        if (windowHandle != 0)
            Native.SetWindowDisplayAffinity(windowHandle, excluded ? AffinityExcludeFromCapture : AffinityNone);
    }

    public void SetOverlayCutout(nint windowHandle, PixelRect? desktopBounds)
    {
        if (desktopBounds is null)
        {
            if (Native.SetWindowRgn(windowHandle, 0, true) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return;
        }
        Rect window;
        if (!Native.GetWindowRect(windowHandle, &window))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint region = Native.CreateRectRgn(0, 0, window.Right - window.Left, window.Bottom - window.Top);
        if (region == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            PixelRect rect = desktopBounds.Value.Offset(-window.Left, -window.Top);
            nint hole = Native.CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
            if (hole == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (Native.CombineRgn(region, region, hole, 4) == 0) // RGN_DIFF
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally { Native.DeleteObject(hole); }
            if (Native.SetWindowRgn(windowHandle, region, true) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            region = 0; // SetWindowRgn transferred ownership to the system.
        }
        finally { if (region != 0) Native.DeleteObject(region); }
    }

    public bool TryGetCursorPosition(out int x, out int y)
    {
        Point point;
        bool ok = Native.GetCursorPos(&point);
        x = point.X;
        y = point.Y;
        return ok;
    }
}
