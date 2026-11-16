using Microsoft.UI.Xaml;
using WSnip.App.Services;
using WSnip.Core.Platform;
using WSnip.Core.Settings;

namespace WSnip.App;

public partial class App : Application
{
    public static PlatformServices? Platform { get; set; }
    public static IReadOnlyList<string> Arguments { get; set; } = [];
    public static AppController? Controller { get; private set; }

    public App()
    {
        InitializeComponent();
        // Mode switches can close every selection window before the next one is ready.
        // AppController decides when capture, background work and the application have ended.
        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LightStudio.Logging.AppLog.Critical("UI", e.Message, e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var platform = Platform ?? throw new InvalidOperationException("Platform services were not configured.");
        Controller = new AppController(this, platform, new SettingsStore(Path.Combine(platform.DataDirectory, "settings.json")));
        Controller.Start(Arguments);
#if DEBUG
        Rendering.PresentationFixture.ShowIfRequested(Controller, Arguments);
#endif
    }
}
