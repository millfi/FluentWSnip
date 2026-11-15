using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSnip.Core.Strings;

namespace WSnip.App.Services;

public static class MessageDialog
{
    public static async void Show(AppWindowBase? owner, string title, string message)
    {
        owner ??= AppWindowBase.Windows.FirstOrDefault(w => w.IsVisible);
        AppWindowBase? temporary = null;
        if (owner?.Content is not FrameworkElement { XamlRoot: not null })
        {
            temporary = new AppWindowBase { Title = title, Content = new Grid() };
            temporary.SetSize(480, 240);
            temporary.Show();
            owner = temporary;
        }
        try
        {
            if (owner.Content is not FrameworkElement root) return;
            if (root.XamlRoot is null)
            {
                var loaded = new TaskCompletionSource();
                root.Loaded += OnLoaded;
                void OnLoaded(object sender, RoutedEventArgs e) { root.Loaded -= OnLoaded; loaded.TrySetResult(); }
                await loaded.Task;
            }
            await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = AppStrings.OK, RequestedTheme = root.ActualTheme }.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            LightStudio.Logging.AppLog.Warning("Dialog", "Could not show the error dialog.", exception);
        }
        finally { temporary?.CloseSafely(); }
    }
}
