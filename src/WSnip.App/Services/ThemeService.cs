using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WSnip.Core.Platform;
using WSnip.Core.Settings;

namespace WSnip.App.Services;

public sealed class ThemeService(IWindowIntegrationService windows) : IDisposable
{
    private readonly List<AppWindowBase> attached = [];
    private readonly Dictionary<AppWindowBase,(ThemeSettings Settings,TypedEventHandler<ThemeSettings,object> Changed)> themes = [];
    private ThemePreference preference;
    private bool micaEnabled = true;
    public bool IsMicaSupported => windows.IsMicaSupported;
    public void Configure(ThemePreference theme,bool mica)
    {
        preference=theme; micaEnabled=mica;
        foreach(var window in attached.ToArray()) Apply(window);
    }
    public void Attach(AppWindowBase window)
    {
        attached.Add(window);
        var theme=ThemeSettings.CreateForWindowId(window.AppWindow.Id);
        TypedEventHandler<ThemeSettings,object> changed=(_,_) => window.DispatcherQueue.TryEnqueue(() => { if(!window.IsClosed) Apply(window); });
        themes.Add(window,(theme,changed));
        theme.Changed+=changed;
        window.Closed+=(_,_) => Detach(window);
        if(window.Content is FrameworkElement root) root.ActualThemeChanged+=(_,_) => ApplyFrame(window);
        Apply(window);
    }
    private void Detach(AppWindowBase window)
    {
        attached.Remove(window);
        if(themes.Remove(window,out var theme)) theme.Settings.Changed-=theme.Changed;
    }
    private void Apply(AppWindowBase window)
    {
        if(window.Content is FrameworkElement root)
            root.RequestedTheme=preference switch {ThemePreference.Light=>ElementTheme.Light,ThemePreference.Dark=>ElementTheme.Dark,_=>ElementTheme.Default};
        bool mica=micaEnabled && IsMicaSupported && themes.TryGetValue(window,out var theme) && !theme.Settings.HighContrast;
        if(window.Content is Panel panel)
        {
            panel.ClearValue(Panel.BackgroundProperty);
            panel.Style=(Style)Application.Current.Resources[mica ? "MicaShellPanelStyle" : "OpaqueShellPanelStyle"];
        }
        else if(window.Content is Control control)
        {
            control.ClearValue(Control.BackgroundProperty);
            control.Style=(Style)Application.Current.Resources[mica ? "MicaShellControlStyle" : "OpaqueShellControlStyle"];
        }
        window.SystemBackdrop=mica ? new MicaBackdrop {Kind=Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt} : null;
        ApplyFrame(window);
    }
    private void ApplyFrame(AppWindowBase window) => windows.SetDarkFrame(window.Handle,window.Content is FrameworkElement {ActualTheme:ElementTheme.Dark});
    public void Dispose() { foreach(var window in attached.ToArray()) Detach(window); }
}
