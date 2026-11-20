using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Window = WSnip.App.Services.AppWindowBase;
using PixelPoint = Windows.Graphics.PointInt32;
using LightStudio.Logging;
using WSnip.App.Capture;
using WSnip.App.Editor;
using WSnip.App.Settings;
using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Core.Settings;
using WSnip.Core.Strings;

namespace WSnip.App.Services;

/// <summary>
/// Owns the running app: the hotkey and tray entry points, the capture flow from frozen screen to
/// delivered snip, the editor and settings windows, and the saved preferences.
/// </summary>
public sealed class AppController : IDisposable
{
    /// <summary>How long hidden windows take to leave the screen, including the compositor's closing animation.</summary>
    private static readonly TimeSpan HiddenWindowsSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>What a shortcut can snip in: the snipping mode setting (null), then each mode.</summary>
    private static readonly SnipMode?[] HotkeyModes = [null, .. Enum.GetValues<SnipMode>()];

    private readonly Application app;
    private readonly PlatformServices platform;
    private readonly SettingsStore store;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly SemaphoreSlim startupSync = new(1, 1);

    /// <summary>The shortcuts of the settings, with why any of them is not in effect.</summary>
    private readonly List<(SnipMode? Mode, HotkeyGesture Gesture, string? Error)> hotkeys = [];
    private EditorWindow? editor;
    private (PointInt32 Position, SizeInt32 Size)? editorBounds;
    private WindowState editorState;
    private SettingsWindow? settingsWindow;
    private NativeTray? tray;
    private Window? backgroundHost;
    private CaptureToolbar? captureToolbar;
    private CancellationTokenSource? captureCancellation;

    private bool capturing;
    private bool exiting;
    private bool hotkeysPaused;
    private int editorOperations;
    private int startupOperations;
    private bool disposed;

    public AppController(Application app, PlatformServices platform, SettingsStore store)
    {
        this.app = app;
        this.platform = platform;
        this.store = store;

        Settings = store.Load();
        Theme = new ThemeService(platform.Windows);
    }

    public AppSettings Settings { get; private set; }

    public ThemeService Theme { get; }

    public PlatformServices Platform => platform;

    /// <summary>Why the capture hotkey could not be registered, if it could not.</summary>
    public string? HotkeyError => HotkeyErrorFor(null);

    public HotkeyGesture? Hotkey => HotkeyFor(null);

    /// <summary>The shortcut that snips in a mode, or in the snipping mode setting when <paramref name="mode"/> is null.</summary>
    public HotkeyGesture? HotkeyFor(SnipMode? mode) => mode is { } snipMode
        ? HotkeyGesture.Parse(Settings.ModeHotkeys.GetValueOrDefault(snipMode))
        : HotkeyGesture.Parse(Settings.Hotkey);

    /// <summary>Why the shortcut of <see cref="HotkeyFor"/> is not in effect, if it is not.</summary>
    public string? HotkeyErrorFor(SnipMode? mode) => hotkeys.Find(h => h.Mode == mode).Error;

    public string SaveFolder => string.IsNullOrWhiteSpace(Settings.SaveFolder) ? platform.Shell.DefaultScreenshotFolder : Settings.SaveFolder;

    public void Start(IReadOnlyList<string> arguments)
    {
        Theme.Configure(Settings.Theme, Settings.UseMica);
        platform.Hotkeys.Pressed += (_, gesture) => dispatcher.TryEnqueue(() => OnHotkeyPressed(gesture));
        RegisterHotkeys();
        platform.SingleInstance.ArgumentsReceived += (_, forwarded) => dispatcher.TryEnqueue(() => HandleArguments(forwarded, firstLaunch: false));
        platform.SingleInstance.StartListening();
        backgroundHost = new Window { Title = "FluentWSnip background" };
        backgroundHost.AppWindow.IsShownInSwitchers = false;
        CreateTray();
        _ = SyncLaunchAtStartupAsync();
        HandleArguments(arguments, firstLaunch: true);
    }

    /// <summary>
    /// Handles a launch: <c>--snip [rectangle|window|fullscreen|freeform|wholewindow]</c> starts a snip,
    /// <c>--background</c> starts in the notification area on Windows, anything else shows the editor.
    /// </summary>
    public void HandleArguments(IReadOnlyList<string> arguments, bool firstLaunch)
    {
        if (exiting)
            return;

        if (OperatingSystem.IsWindows() && arguments.Any(argument =>
            Uri.TryCreate(argument, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme.Equals("ms-screenclip", StringComparison.OrdinalIgnoreCase)))
        {
            _ = StartSnipAsync();
            return;
        }

        int snip = arguments.ToList().FindIndex(a => a.Equals("--snip", StringComparison.OrdinalIgnoreCase));
        if (snip >= 0)
        {
            SnipMode? mode = snip + 1 < arguments.Count && Enum.TryParse(arguments[snip + 1], ignoreCase: true, out SnipMode parsed)
                ? parsed
                : null;
            _ = StartSnipAsync(mode);
        }
        else if (!(OperatingSystem.IsWindows() && firstLaunch && arguments.Contains("--background", StringComparer.OrdinalIgnoreCase)))
        {
            ShowEditor();
        }
    }

    /// <summary>Freezes the screen, lets the user pick a region and delivers the snip.</summary>
    /// <remarks>Whole-window snips leave the screen live and wait for a click on a window instead.</remarks>
    public async Task StartSnipAsync(SnipMode? mode = null, int? delaySeconds = null)
    {
        if (capturing || exiting)
            return;
        capturing = true;
        List<(Window Window, Window? Owner)>? hidden = null;
        CaptureToolbar? toolbar = null;
        using var selectionCancellation = new CancellationTokenSource();
        captureCancellation = selectionCancellation;
        try
        {
            SnipMode initialMode = mode ?? Settings.Mode;
            int delay = delaySeconds ?? 0;
            hidden = HideWindows();
            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay), selectionCancellation.Token);
            else if (hidden.Count > 0)
                await Task.Delay(HiddenWindowsSettle, selectionCancellation.Token);

            if (exiting)
                return;

            platform.Windows.TryGetCursorPosition(out int pointerX, out int pointerY);
            toolbar = captureToolbar = new CaptureToolbar(new PixelPoint(pointerX, pointerY), initialMode, Theme);
            toolbar.ModeChanged += selected => UpdateSettings(Settings.With(s => s.Mode = selected));
            toolbar.CancelRequested += selectionCancellation.Cancel;
            if (!await toolbar.ShowAsync()) return;

            while (!toolbar.IsCancelled && !exiting)
            {
                if (toolbar.Mode == SnipMode.WholeWindow)
                {
                    SnipMode? nextMode = await SnipWholeWindowAsync(toolbar,
                        restoreEditor: hidden.Any(entry => ReferenceEquals(entry.Window, editor)), selectionCancellation.Token);
                    if (nextMode is null)
                        return;
                    continue;
                }

                // The toolbar stays visible, but display affinity excludes it from this snapshot.
                using ScreenSnapshot snapshot = await platform.Capture.CaptureAsync(
                    new CaptureOptions { IncludeCursor = Settings.IncludeCursor }, selectionCancellation.Token);
                if (toolbar.IsCancelled || exiting) return;
                if (toolbar.Mode == SnipMode.WholeWindow) continue;
                platform.Windows.TryGetCursorPosition(out int x, out int y);
                using var session = new OverlaySession(snapshot, toolbar, Theme);
                SnipSelection? selection = await session.ShowAsync(new PixelPoint(x, y));

                if (selection is null)
                {
                    // The toolbar may already have changed again while this background was closing.
                    if (!toolbar.IsCancelled && session.Mode == SnipMode.WholeWindow) continue;
                    return;
                }

                toolbar.Hide();
                // A confirmed selection replaces the old document before composition and encoding.
                // Cancellation still preserves it, but its pixels never overlap a new delivery.
                editor?.Clear();
                using Snip snip = await Task.Run(() => SnipComposer.Compose(snapshot, selection.Region, selection.Outline));
                snapshot.Dispose();
                if (exiting)
                    return;
                AppLog.Information("Snip", $"{selection.Mode} snip {snip.Width}x{snip.Height}: {snip.Statistics}");
                await DeliverAsync(snip, restoreEditor: hidden?.Any(entry => ReferenceEquals(entry.Window, editor)) == true);
                return;
            }
        }
        catch (OperationCanceledException exception)
        {
            AppLog.Information("Snip", $"The snip was cancelled: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Error("Snip", "Taking a snip failed.", exception);
            toolbar?.CloseSafely();
            if (!exiting)
                MessageDialog.Show(editor is { IsVisible: true } ? editor : null, "FluentWSnip", string.Format(AppStrings.CaptureFailed, exception.Message));
        }
        finally
        {
            toolbar?.CloseSafely();
            if (hidden is not null)
                RestoreWindows(hidden);
            captureToolbar = null;
            captureCancellation = null;
            capturing = false;
            FinishExitWhenIdle();
        }
    }

    public void ShowEditor(EditorDocument? document = null, string? savedPath = null)
    {
        EditorWindow window = EnsureEditor();
        if (document is not null)
            window.Load(document, savedPath);
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void ShowSettings()
    {
        if (settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        settingsWindow = new SettingsWindow(this);
        Theme.Attach(settingsWindow);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        if (editor is { IsVisible: true } owner)
            settingsWindow.Show(owner);
        else
            settingsWindow.Show();
    }

    public void UpdateSettings(AppSettings updated)
    {
        bool themeChanged = updated.Theme != Settings.Theme || updated.UseMica != Settings.UseMica;
        bool hotkeysChanged = updated.Hotkey != Settings.Hotkey || !ReferenceEquals(updated.ModeHotkeys, Settings.ModeHotkeys);
        bool startupChanged = updated.LaunchAtStartup != Settings.LaunchAtStartup;
        Settings = updated;
        store.Save(updated);
        if (themeChanged)
            Theme.Configure(updated.Theme, updated.UseMica);
        if (hotkeysChanged)
            RegisterHotkeys();
        if (startupChanged)
            _ = SyncLaunchAtStartupAsync();
        editor?.OnSettingsChanged();
    }

    /// <summary>
    /// Gives a mode a shortcut, or the snipping mode setting when <paramref name="mode"/> is null,
    /// or takes it away when <paramref name="gesture"/> is null. A shortcut that another of WSnip's
    /// shortcuts, another app or Windows already uses is refused: nothing is saved or applied, and
    /// the reason is returned.
    /// </summary>
    public string? ChangeHotkey(SnipMode? mode, HotkeyGesture? gesture)
    {
        if (gesture is not null)
        {
            if (gesture == HotkeyFor(mode))
                return null;
            foreach (SnipMode? other in HotkeyModes)
            {
                if (other != mode && HotkeyFor(other) == gesture)
                    return string.Format(AppStrings.HotkeyConflict, gesture, HotkeyPurpose(other));
            }

            // Registering the shortcut before saving it finds out whether the system lets WSnip
            // have it; applying the settings then registers every shortcut afresh.
            if (platform.Hotkeys.Register(gesture) is { } refused)
                return refused;
        }

        UpdateSettings(Settings.With(s =>
        {
            if (mode is not { } snipMode)
            {
                s.Hotkey = gesture?.ToString() ?? string.Empty;
                return;
            }

            var modeHotkeys = new Dictionary<SnipMode, string>(s.ModeHotkeys);
            if (gesture is null)
                modeHotkeys.Remove(snipMode);
            else
                modeHotkeys[snipMode] = gesture.ToString();
            s.ModeHotkeys = modeHotkeys;
        }));
        return null;
    }

    /// <summary>
    /// Stops WSnip's shortcuts from starting snips, so that their key combinations reach its own
    /// windows, as when the user records a shortcut; or starts them again.
    /// </summary>
    public void PauseHotkeys(bool paused)
    {
        if (paused == hotkeysPaused)
            return;
        hotkeysPaused = paused;
        if (paused)
            platform.Hotkeys.UnregisterAll();
        else
            RegisterHotkeys();
    }

    /// <summary>Places the SDR rendition on the clipboard.</summary>
    public async Task CopyAsync(Snip snip)
    {
        ToneMapSettings toneMap = Settings.DefaultToneMap();
        using ClipboardImage image = await Task.Run(() =>
        {
            using Rendition rendition = SnipExporter.BuildSdr(snip, toneMap, flattenAlpha: false);
            return ClipboardImage.FromRendition(rendition);
        });
        var system = platform.Clipboard ?? throw new InvalidOperationException(AppStrings.ClipboardUnavailable);
        system.SetImage(image, EnsureEditor().Handle);
    }
    /// <summary>Writes a snip in the format that suits its content into the screenshot folder.</summary>
    public async Task<string> AutoSaveAsync(Snip snip)
    {
        SnipFormat format = Settings.FormatFor(snip);
        if (!SnipExporter.IsAvailable(format))
            format = snip.Statistics.HasHdr ? SnipFormat.Jpeg : SnipFormat.Png;
        string folder = SaveFolder;
        Directory.CreateDirectory(folder);
        string path = UniquePath(Path.Combine(folder, SnipExporter.DefaultFileName(snip.CapturedAt, format)));
        await SnipExporter.ExportFileAsync(path, snip, format, Settings.ExportOptions());
        AppLog.Information("Snip", $"Saved {path}.");
        return path;
    }

    public async Task SaveAsync(Snip snip, string path, SnipFormat format)
    {
        await SnipExporter.ExportFileAsync(path, snip, format, Settings.ExportOptions());
        AppLog.Information("Snip", $"Saved {path}.");
    }

    public void Exit()
    {
        if (exiting)
            return;
        exiting = true;
        captureCancellation?.Cancel();
        captureToolbar?.CloseSafely();
        FinishExitWhenIdle();
    }

    private void FinishExitWhenIdle()
    {
        if (!exiting || disposed || capturing || editorOperations != 0 || startupOperations != 0)
            return;
        AppLog.Information("Lifetime", "Exiting FluentWSnip.");
        Dispose();
        foreach (var window in Window.Windows.ToArray()) window.CloseSafely();
        app.Exit();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        editor?.Clear();
        settingsWindow?.Close();
        backgroundHost?.CloseSafely();
        backgroundHost = null;
        tray?.Dispose();
        tray = null;

        platform.Hotkeys.Dispose();
        platform.SingleInstance.Dispose();
        Theme.Dispose();
        // An active capture owns the device until its asynchronous cleanup completes.
        if (!capturing)
            (platform.Capture as IDisposable)?.Dispose();
    }

    /// <summary>Lets an accepted copy or save finish before shutdown.</summary>
    internal IDisposable BeginEditorOperation()
    {
        editorOperations++;
        return new EditorOperation(this);
    }

    private sealed class EditorOperation(AppController controller) : IDisposable
    {
        private AppController? owner = controller;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref owner, null) is not { } current)
                return;
            current.editorOperations--;
            current.FinishExitWhenIdle();
        }
    }

    /// <summary>
    /// Waits for a click on a window while the screen stays live, then reads that window whole
    /// from the compositor, with its transparency.
    /// </summary>
    /// <remarks>
    /// The editor and settings windows stay hidden; the shared capture toolbar remains available.
    /// The capture flow restores the hidden windows afterward.
    /// </remarks>
    private async Task<SnipMode?> SnipWholeWindowAsync(CaptureToolbar toolbar, bool restoreEditor, CancellationToken cancellationToken)
    {
        if (!platform.WindowPicker.IsSupported)
            throw new NotSupportedException(AppStrings.WindowPickingUnsupported);

        using var cancellation = new CancellationTokenSource();
        CapturedWindow? window;
        bool modeChanged = false;
        void ModeChanged(SnipMode _) { modeChanged = true; cancellation.Cancel(); }
        toolbar.ModeChanged += ModeChanged;
        toolbar.CancelRequested += cancellation.Cancel;
        try
        {
            if (toolbar.IsCancelled) return null;
            if (toolbar.Mode != SnipMode.WholeWindow) return toolbar.Mode;
            try
            {
                window = await platform.WindowPicker.PickAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                window = null;
            }
        }
        finally
        {
            toolbar.ModeChanged -= ModeChanged;
            toolbar.CancelRequested -= cancellation.Cancel;
        }

        if (toolbar.IsCancelled || exiting) return null;
        if (modeChanged || toolbar.Mode != SnipMode.WholeWindow) return toolbar.Mode;
        if (window is null) return null;
        toolbar.Hide();
        using WindowCapture capture = await platform.Capture.CaptureWindowAsync(
            window, new CaptureOptions { IncludeCursor = Settings.IncludeCursor }, cancellationToken);
        editor?.Clear();
        using Snip snip = await Task.Run(() => SnipComposer.FromWindow(capture));
        capture.Dispose();
        if (exiting)
            return null;
        AppLog.Information("Snip", $"Whole window snip of '{window.Title}' {snip.Width}x{snip.Height}: {snip.Statistics}");
        await DeliverAsync(snip, restoreEditor);
        return null;
    }

    /// <summary>Hides the app's visible windows, returning them in opening order with their owners.</summary>
    private List<(Window Window, Window? Owner)> HideWindows()
    {
        editor?.PrepareForCapture();
        List<(Window Window, Window? Owner)> hidden = Window.Windows
            .Where(w => w.IsVisible)
            .Select(w => (w, (Window?)null))
            .ToList();

        // Owned windows go first, so an owner is never hidden while its dialogs are still up.
        for (int i = hidden.Count - 1; i >= 0; i--)
            hidden[i].Window.Hide();
        return hidden;
    }

    /// <summary>Shows hidden windows again, owners first, unless they were closed or shown meanwhile.</summary>
    private void RestoreWindows(List<(Window Window, Window? Owner)> hidden)
    {
        if (exiting)
            return;
        foreach ((Window window, Window? owner) in hidden)
        {
            if (window.IsVisible || !Window.Windows.Contains(window))
                continue;
            if (owner is { IsVisible: true } && Window.Windows.Contains(owner))
                window.Show(owner);
            else
                window.Show();
        }
    }

　    private async Task DeliverAsync(Snip snip, bool restoreEditor = false)
    {
        string? saved = null;
        string? problem = null;
        if (Settings.CopyToClipboard)
        {
            try
            {
                await CopyAsync(snip);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLog.Error("Snip", "Copying the snip failed.", exception);
                problem = string.Format(AppStrings.CopyFailed, exception.Message);
            }
        }

        if (Settings.AutoSave)
        {
            try
            {
                saved = await AutoSaveAsync(snip);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLog.Error("Snip", "Saving the snip failed.", exception);
                problem = string.Format(AppStrings.AutoSaveFailed, SaveFolder, exception.Message);
            }
        }

        // The editor keeps the snip when it could not be delivered.
        if (exiting)
            return;
        if (Settings.OpenEditorAfterCapture || editor is { IsVisible: true } || (restoreEditor && editor is not null) || problem is not null ||
            (!Settings.CopyToClipboard && !Settings.AutoSave))
        {
            var document = new EditorDocument(snip);
            try
            {
                ShowEditor(document, saved);
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        if (problem is not null)
            MessageDialog.Show(editor is { IsVisible: true } ? editor : null, "FluentWSnip", problem);
    }

    private EditorWindow EnsureEditor()
    {
        if (editor is not null)
            return editor;

        var window = new EditorWindow(this);
        editor = window;
        if (editorBounds is { } bounds)
        {
            window.Position = bounds.Position;
            window.PixelSize = bounds.Size;
        }
        window.WindowState = editorState;
        Theme.Attach(window);
        void RememberBounds()
        {
            if (window.IsVisible && window.WindowState == WindowState.Normal)
                editorBounds = (window.Position, window.PixelSize);
        }
        window.PositionChanged += (_, _) => RememberBounds();
        window.SizeChanged += (_, _) => RememberBounds();
        window.Opened += (_, _) => RememberBounds();
        window.Closing += (_, _) => editorState = window.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        window.Closed += (_, _) => { if (ReferenceEquals(editor, window)) editor = null; };
        return window;
    }

    /// <summary>
    /// Registers the shortcuts of the settings, the snipping mode setting's first. One that repeats
    /// an earlier shortcut, as only an edited settings file can, is left out.
    /// </summary>
    private void RegisterHotkeys()
    {
        platform.Hotkeys.UnregisterAll();
        hotkeys.Clear();
        if (!platform.Hotkeys.IsSupported)
            return;
        foreach (SnipMode? mode in HotkeyModes)
        {
            if (HotkeyFor(mode) is not { } gesture)
                continue;
            int earlier = hotkeys.FindIndex(h => h.Gesture == gesture);
            string? error = earlier >= 0
                ? string.Format(AppStrings.HotkeyConflict, gesture, HotkeyPurpose(hotkeys[earlier].Mode))
                : platform.Hotkeys.Register(gesture);
            hotkeys.Add((mode, gesture, error));
            if (error is not null)
                AppLog.Warning("Hotkey", error);
        }

        if (hotkeys.Exists(h => h.Error is null))
            AppLog.Information("Hotkey", $"Registered {string.Join(", ", hotkeys.Where(h => h.Error is null).Select(h => $"{h.Gesture} for {HotkeyPurpose(h.Mode)}"))}.");

        // Registering found out which shortcuts the system refuses; while paused, none is in effect.
        if (hotkeysPaused)
            platform.Hotkeys.UnregisterAll();
    }

    private void OnHotkeyPressed(HotkeyGesture gesture)
    {
        foreach ((SnipMode? mode, HotkeyGesture registered, string? error) in hotkeys)
        {
            if (error is null && registered == gesture)
            {
                _ = StartSnipAsync(mode);
                return;
            }
        }
    }

    private static string HotkeyPurpose(SnipMode? mode) => mode switch
    {
        SnipMode.Rectangle => AppStrings.RectanglePurpose,
        SnipMode.Window => AppStrings.WindowPurpose,
        SnipMode.Fullscreen => AppStrings.FullscreenPurpose,
        SnipMode.Freeform => AppStrings.FreeformPurpose,
        SnipMode.WholeWindow => AppStrings.WholeWindowPurpose,
        _ => AppStrings.DefaultModePurpose,
    };

    private async Task SyncLaunchAtStartupAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;
        startupOperations++;
        await startupSync.WaitAsync();
        try
        {
            bool enabled = await platform.Shell.IsLaunchAtStartupEnabledAsync();
            bool wanted = Settings.LaunchAtStartup;
            if (Environment.ProcessPath is not { } executable || enabled == wanted)
                return;
            await platform.Shell.SetLaunchAtStartupAsync(wanted, executable, "--background");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Warning("Startup", "Launch at startup could not be changed.", exception);
        }
        finally
        {
            startupSync.Release();
            startupOperations--;
            dispatcher.TryEnqueue(FinishExitWhenIdle);
        }
    }

    private void CreateTray() => tray = new NativeTray(
    [
        (AppStrings.NewSnip, () => _ = StartSnipAsync()),
        (AppStrings.OpenWSnip, () => ShowEditor()),
        (AppStrings.OpenFolder, () => platform.Shell.OpenFolder(SaveFolder)),
        (AppStrings.Settings, ShowSettings),
        (string.Empty, null),
        (AppStrings.Exit, () => dispatcher.TryEnqueue(Exit)),
    ], () => ShowEditor());
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
