using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace WSnip.App.Services;

internal static class InputKeys
{
    public static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    public static bool Control => Down(VirtualKey.Control);
    public static bool Shift => Down(VirtualKey.Shift);
    public static bool Alt => Down(VirtualKey.Menu);
    public static bool Windows => Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);
}
