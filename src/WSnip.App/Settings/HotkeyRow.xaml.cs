using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WSnip.App.Services;

using WSnip.Core.Platform;

namespace WSnip.App.Settings;

/// <summary>
/// A settings row for a global shortcut: the user clicks the box and presses a key combination,
/// chooses Print Screen or clears it. The owner decides whether to take the combination.
/// </summary>
public partial class HotkeyRow : UserControl
{
    private HotkeyGesture? gesture;
    private string? caption;
    private string? warning;

    public HotkeyRow()
    {
        InitializeComponent();
        GestureBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnGestureKeyDown), true);
        GestureBox.GotFocus += (_, _) => RecordingChanged?.Invoke(this, EventArgs.Empty);
        GestureBox.LostFocus += (_, _) => RecordingChanged?.Invoke(this, EventArgs.Empty);
        PrintScreenButton.Click += (_, _) => GestureChosen?.Invoke(this, new HotkeyGesture("PrintScreen"));
        ClearButton.Click += (_, _) => GestureChosen?.Invoke(this, null);
    }

    /// <summary>Raised with the combination the user chose, or null when they cleared the shortcut.</summary>
    public event EventHandler<HotkeyGesture?>? GestureChosen;

    /// <summary>Raised when the box gains or loses the keyboard focus, and with it the key combinations pressed.</summary>
    public event EventHandler? RecordingChanged;

    /// <summary>Whether the key combinations the user presses go to the box.</summary>
    public bool IsRecording => GestureBox.FocusState != FocusState.Unfocused;

    public string? Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>What the shortcut does, shown while there is no warning.</summary>
    public string? Caption
    {
        get => caption;
        set
        {
            caption = value;
            ShowInfo();
        }
    }

    /// <summary>Why the shortcut is not in effect or was not changed, shown instead of the caption.</summary>
    public string? Warning
    {
        get => warning;
        set
        {
            warning = value;
            ShowInfo();
        }
    }

    /// <summary>The shortcut shown in the box.</summary>
    public HotkeyGesture? Gesture
    {
        get => gesture;
        set
        {
            gesture = value;
            GestureBox.Text = value?.ToString() ?? string.Empty;
        }
    }

    /// <summary>Whether the shortcut can be changed here, rather than only described.</summary>
    public bool IsEditorVisible
    {
        get => Editor.Visibility == Visibility.Visible;
        set => Editor.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowInfo()
    {
        InfoText.Text = warning ?? caption;
        InfoText.Opacity = warning is null ? 0.75 : 1;
    }

    private void OnGestureKeyDown(object? sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Key is VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return;
        if (e.Key is VirtualKey.Escape or VirtualKey.Tab)
        {
            e.Handled = e.Key == VirtualKey.Escape;
            return;
        }

        string? name = KeyName(e.Key);
        if (name is null)
            return;
        GestureChosen?.Invoke(this, new HotkeyGesture(name,
            Control: InputKeys.Control,
            Shift: InputKeys.Shift,
            Alt: InputKeys.Alt,
            Windows: InputKeys.Windows));
    }

    private static string? KeyName(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => key.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F24 => key.ToString(),
        VirtualKey.Snapshot => "PrintScreen",
        VirtualKey.Space => "Space",
        VirtualKey.Insert => "Insert",
        VirtualKey.Delete => "Delete",
        VirtualKey.Home => "Home",
        VirtualKey.End => "End",
        VirtualKey.PageUp => "PageUp",
        VirtualKey.PageDown => "PageDown",
        VirtualKey.Pause => "Pause",
        VirtualKey.Scroll => "ScrollLock",
        _ => null,
    };
}
