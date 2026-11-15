using LightStudio.FfmpegShim;
using LightStudio.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WSnip.Windows;

namespace WSnip.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var platform = WindowsPlatform.Create();
        AppLog.Configure(writeToConsole: false, Path.Combine(platform.DataDirectory, "logs", "wsnip.log"));
        try
        {
            if (WindowsPackage.IsStartupActivation()) args = [.. args, "--background"];
            if (WindowsPackage.ProtocolActivationUri() is { } uri) args = [.. args, uri];
            if (!platform.SingleInstance.TryClaim() && platform.SingleInstance.ForwardAsync(args).GetAwaiter().GetResult())
            {
                platform.SingleInstance.Dispose();
                platform.Hotkeys.Dispose();
                (platform.Capture as IDisposable)?.Dispose();
                return 0;
            }
            FfmpegLog.Attach();
            App.Platform = platform;
            App.Arguments = args;
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return 0;
        }
        catch (Exception exception)
        {
            AppLog.Critical("Startup", "WSnip terminated unexpectedly.", exception);
            throw;
        }
        finally { AppLog.Shutdown(); }
    }
}
