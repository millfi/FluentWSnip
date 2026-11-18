using System;
using System.Runtime.InteropServices;

public static class CaptureTestNative
{
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        uint ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string applicationId,
                                 [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options);
    }

    [DllImport("ole32.dll")]
    private static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object manager,
                                                          IntPtr reserved);

    public static uint ActivatePackage(string applicationId, string arguments)
    {
        var classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        object manager = Activator.CreateInstance(Type.GetTypeFromCLSID(classId));
        try
        {
            CoAllowSetForegroundWindow(manager, IntPtr.Zero);
            return ((IApplicationActivationManager)manager).ActivateApplication(applicationId, arguments, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetFrameBounds(IntPtr hwnd, uint attribute, out Rect rect, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetCloaked(IntPtr hwnd, uint attribute, out int value, int size);

    public static void UsePhysicalPixels()
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
    }

    public static Rect FrameBounds(IntPtr hwnd)
    {
        Rect bounds;
        Marshal.ThrowExceptionForHR(GetFrameBounds(hwnd, 9, out bounds, Marshal.SizeOf(typeof(Rect))));
        return bounds;
    }

    public static bool IsRevealed(IntPtr hwnd)
    {
        int cloaked;
        return GetCloaked(hwnd, 14, out cloaked, sizeof(int)) == 0 && cloaked == 0;
    }

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    public static byte[] TryReadClipboardPng()
    {
        uint png = RegisterClipboardFormat("PNG");
        if (png == 0 || !OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            IntPtr memory = GetClipboardData(png);
            if (memory == IntPtr.Zero)
                return null;

            int size = checked((int)GlobalSize(memory).ToUInt64());
            IntPtr data = GlobalLock(memory);
            if (data == IntPtr.Zero)
                return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
