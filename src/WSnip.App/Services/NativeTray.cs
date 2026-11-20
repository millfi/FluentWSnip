using System.Runtime.InteropServices;
using LightStudio.Logging;

namespace WSnip.App.Services;

/// <summary>Owns the notification icon and the native window that receives its callbacks.</summary>
internal sealed class NativeTray : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly nint hwnd;
    private readonly nint icon;
    private readonly SubclassProc callback;
    private readonly (string Text, Action? Action)[] commands;
    private readonly Action open;
    private readonly uint taskbarCreated;
    private bool disposed;

    public NativeTray((string Text, Action? Action)[] commands, Action open)
    {
        this.commands = commands;
        this.open = open;
        hwnd = CreateWindowEx(0, "STATIC", "FluentWSnip notification area", unchecked((int)0x80000000), 0, 0, 0, 0, 0, 0, 0, 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        callback = WndProc;
        if (!SetWindowSubclass(hwnd, callback, 1, 0))
        {
            DestroyWindow(hwnd);
            throw new InvalidOperationException("Could not subclass the notification area window.");
        }
        icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "wsnip.ico"), 1, 0, 0, 0x10 | 0x40);
        if (icon == 0)
        {
            Dispose();
            throw new InvalidOperationException("Could not load the notification area icon.");
        }
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Add();
    }
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1,
        Flags = 0x1 | 0x2 | 0x4, CallbackMessage = Callback, Icon = icon, Tip = "FluentWSnip",
        Info = "", InfoTitle = "",
    };
    private void Add()
    {
        var data = Data();
        if (!ShellNotifyIcon(0, ref data))
            AppLog.Warning("Tray", "Could not register the FluentWSnip icon in the notification area.");
    }
    private nint WndProc(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        if (message == taskbarCreated) Add();
        if (message == Callback)
        {
            uint notification = (uint)(long)lparam & 0xffff;
            if (notification == 0x0203) open();
            else if (notification == 0x0205 || notification == 0x007B)
            {
                nint menu = CreatePopupMenu();
                if (menu == 0)
                    return 0;
                uint selected;
                try
                {
                    for (int i = 0; i < commands.Length; i++)
                        AppendMenu(menu, commands[i].Action is null ? 0x800u : 0u, (nuint)(i + 1), commands[i].Text);
                    GetCursorPos(out var point);
                    SetForegroundWindow(hwnd);
                    selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
                    PostMessage(hwnd, 0, 0, 0);
                }
                finally { DestroyMenu(menu); }
                if (selected > 0 && selected <= commands.Length)
                    commands[selected - 1].Action?.Invoke();
            }
            return 0;
        }
        return DefSubclassProc(window, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        RemoveWindowSubclass(hwnd, callback, 1);
        DestroyWindow(hwnd);
        if (icon != 0) DestroyIcon(icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string name, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
}
