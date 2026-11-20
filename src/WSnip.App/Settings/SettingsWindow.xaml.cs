using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;
using WSnip.App.Capture;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;
using WSnip.Core.Settings;
using WSnip.Core.Strings;

namespace WSnip.App.Settings;

/// <summary>Preferences; every change applies and is saved immediately.</summary>
public partial class SettingsWindow : AppWindowBase
{
    internal static readonly (SdrToneMapping Mode, string Label, string Description)[] ToneMappings =
    [
        (SdrToneMapping.Adaptive, AppStrings.Adaptive, AppStrings.AdaptiveDescription),
        (SdrToneMapping.Global, AppStrings.GlobalToneMapping, AppStrings.GlobalToneMappingDescription),
        (SdrToneMapping.Clip, AppStrings.ClipHighlights, AppStrings.ClipHighlightsDescription),
    ];

    private readonly AppController controller;
    private readonly List<(HotkeyRow Row, SnipMode? Mode)> hotkeyRows = [];

    /// <summary>The shortcut WSnip last refused and why, shown on its row until the next shortcut change.</summary>
    private (HotkeyRow Row, string Text)? hotkeyRefusal;
    private bool loading;

    public SettingsWindow()
        : this(App.Controller ?? throw new InvalidOperationException("The app has not started."))
    {
    }

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        Title = AppStrings.SettingsTitle;
        SetSize(760, 820);
        SetMinimumSize(620, 440);

        foreach (SnipModeOption option in SnipModeOption.All)
            ModeBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Mode });
        foreach (DelayOption option in DelayOption.All)
            DelayBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Seconds });
        foreach (SnipFormat format in SnipExporter.AllFormats.Where(SnipExporter.IsAvailable))
        {
            SdrFormatBox.Items.Add(new ComboBoxItem { Content = SnipExporter.DisplayName(format), Tag = format });
            HdrFormatBox.Items.Add(new ComboBoxItem { Content = SnipExporter.DisplayName(format), Tag = format });
        }

        foreach ((SdrToneMapping mode, string label, _) in ToneMappings)
            ToneMappingBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        foreach (ToneMapCurve curve in ToneMapCurves.All)
            ToneCurveBox.Items.Add(new ComboBoxItem { Content = ToneMapCurves.Name(curve), Tag = curve });
        foreach (ThemePreference theme in Enum.GetValues<ThemePreference>())
            ThemeBox.Items.Add(new ComboBoxItem
            {
                Content = theme switch
                {
                    ThemePreference.Light => AppStrings.ThemeLight,
                    ThemePreference.Dark => AppStrings.ThemeDark,
                    _ => AppStrings.ThemeSystem,
                },
                Tag = theme,
            });
        MicaRow.Visibility = (controller.Theme.IsMicaSupported) ? Visibility.Visible : Visibility.Collapsed;
        if (controller.Platform.Hotkeys.IsSupported)
        {
            AddHotkeyRow(DefaultHotkeyRow, null);
            foreach (SnipModeOption option in SnipModeOption.All)
            {
                var row = new HotkeyRow { Title = option.Label, Caption = option.Description };
                ModeHotkeyRows.Children.Add(row);
                AddHotkeyRow(row, option.Mode);
            }
        }
        else
        {
            DefaultHotkeyRow.IsEditorVisible = false;
            DefaultHotkeyRow.Caption = AppStrings.SystemShortcutsCaption;
            ModeHotkeySection.Visibility = (false) ? Visibility.Visible : Visibility.Collapsed;
        }

        ScreenClippingRow.Visibility = (OperatingSystem.IsWindows()) ? Visibility.Visible : Visibility.Collapsed;
        ScreenClippingButton.Click += (_, _) => controller.Platform.Shell.OpenScreenClippingSettings();

        Load();

        Activated += (_, e) => PauseHotkeysWhileRecording(e.WindowActivationState != WindowActivationState.Deactivated);
        Closed += (_, _) => controller.PauseHotkeys(false);
        ModeBox.SelectionChanged += (_, _) => Update(s => s.Mode = Selected<SnipMode>(ModeBox));
        DelayBox.SelectionChanged += (_, _) => Update(s => s.DelaySeconds = Selected<int>(DelayBox));
        CursorSwitch.Toggled += (_, _) => Update(s => s.IncludeCursor = CursorSwitch.IsOn == true);
        CopySwitch.Toggled += (_, _) => Update(s => s.CopyToClipboard = CopySwitch.IsOn == true);
        EditorSwitch.Toggled += (_, _) => Update(s => s.OpenEditorAfterCapture = EditorSwitch.IsOn == true);
        PixelInfoSwitch.Toggled += (_, _) => Update(s => s.ShowPixelInfo = PixelInfoSwitch.IsOn == true);
        AutoSaveSwitch.Toggled += (_, _) => Update(s => s.AutoSave = AutoSaveSwitch.IsOn == true);
        FolderButton.Click += async (_, _) => await PickFolderAsync();
        SdrFormatBox.SelectionChanged += (_, _) => Update(s => s.SdrFormat = Selected<SnipFormat>(SdrFormatBox));
        HdrFormatBox.SelectionChanged += (_, _) => Update(s => s.HdrFormat = Selected<SnipFormat>(HdrFormatBox));
        QualitySlider.ValueChanged += (_, _) =>
        {
            QualityText.Text = ((int)QualitySlider.Value).ToString(System.Globalization.CultureInfo.CurrentCulture);
            Update(s => s.Quality = (int)QualitySlider.Value);
        };
        ToneMappingBox.SelectionChanged += (_, _) => Update(s => s.ToneMapping = Selected<SdrToneMapping>(ToneMappingBox));
        ToneCurveBox.SelectionChanged += (_, _) => Update(s => s.ToneMapCurve = Selected<ToneMapCurve>(ToneCurveBox));
        ThemeBox.SelectionChanged += (_, _) => Update(s => s.Theme = Selected<ThemePreference>(ThemeBox));
        MicaSwitch.Toggled += (_, _) => Update(s => s.UseMica = MicaSwitch.IsOn == true);
        StartupSwitch.Toggled += (_, _) => Update(s => s.LaunchAtStartup = StartupSwitch.IsOn == true);
    }

    private void Load()
    {
        loading = true;
        AppSettings settings = controller.Settings;
        foreach ((HotkeyRow row, SnipMode? mode) in hotkeyRows)
        {
            row.Gesture = controller.HotkeyFor(mode);
            row.Warning = hotkeyRefusal is { } refusal && refusal.Row == row ? refusal.Text : controller.HotkeyErrorFor(mode);
        }

        Select(ModeBox, settings.Mode);
        Select(DelayBox, settings.DelaySeconds);
        CursorSwitch.IsOn = settings.IncludeCursor;
        CopySwitch.IsOn = settings.CopyToClipboard;
        EditorSwitch.IsOn = settings.OpenEditorAfterCapture;
        PixelInfoSwitch.IsOn = settings.ShowPixelInfo;
        AutoSaveSwitch.IsOn = settings.AutoSave;
        FolderText.Text = controller.SaveFolder;
        Select(SdrFormatBox, settings.SdrFormat);
        Select(HdrFormatBox, settings.HdrFormat);
        CodecWarning.Visibility = (settings.SdrFormat is SnipFormat.Heic or SnipFormat.Avif ||
            settings.HdrFormat is SnipFormat.Heic or SnipFormat.Avif) ? Visibility.Visible : Visibility.Collapsed;
        QualitySlider.Value = settings.Quality;
        QualityText.Text = settings.Quality.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Select(ToneMappingBox, settings.ToneMapping);
        ToneMappingInfo.Text = ToneMappings.First(t => t.Mode == settings.ToneMapping).Description;
        Select(ToneCurveBox, settings.ToneMapCurve);
        ToneCurveInfo.Text = ToneMapCurves.Description(settings.ToneMapCurve) + " " + AppStrings.ToneCurveCaption;
        ToneCurveBox.IsEnabled = settings.ToneMapping != SdrToneMapping.Clip;
        Select(ThemeBox, settings.Theme);
        MicaSwitch.IsOn = settings.UseMica;
        StartupSwitch.IsOn = settings.LaunchAtStartup;

        string version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string missing = string.Join(", ", SnipExporter.AllFormats.Where(f => !SnipExporter.IsAvailable(f)).Select(SnipExporter.DisplayName));
        AboutText.Text = string.Format(AppStrings.About, version.Split('+')[0]) +
            (missing.Length > 0 ? " " + string.Format(AppStrings.UnavailableFormats, missing) : string.Empty);
        loading = false;
    }

    private void Update(Action<AppSettings> change)
    {
        if (loading)
            return;
        controller.UpdateSettings(controller.Settings.With(change));
        Load();
    }

    /// <summary>Lets a row change the shortcut for a mode, or for the default mode when <paramref name="mode"/> is null.</summary>
    private void AddHotkeyRow(HotkeyRow row, SnipMode? mode)
    {
        hotkeyRows.Add((row, mode));
        row.RecordingChanged += (_, _) => PauseHotkeysWhileRecording(IsActive);
        row.GestureChosen += (_, gesture) =>
        {
            string? refused = controller.ChangeHotkey(mode, gesture);
            hotkeyRefusal = refused is null ? null : (row, string.Format(AppStrings.HotkeyRefusal, refused));
            Load();
        };
    }

    /// <summary>
    /// Pauses WSnip's shortcuts while a box records one, so that the combinations the user presses,
    /// WSnip's own included, reach the box instead of starting snips.
    /// </summary>
    private void PauseHotkeysWhileRecording(bool active) =>
        controller.PauseHotkeys(active && hotkeyRows.Exists(h => h.Row.IsRecording));

    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) Update(s => s.SaveFolder = folder.Path);
    }
    private static T Selected<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : default!;

    private static void Select<T>(ComboBox box, T value)
    {
        foreach (ComboBoxItem item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }
}
