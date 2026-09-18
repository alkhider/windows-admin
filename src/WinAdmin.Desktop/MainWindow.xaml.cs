using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WinAdmin.Core.Infrastructure;

namespace WinAdmin.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowState = WindowState.Maximized;
        Loaded += OnLoaded;
        Closed += (_, _) => PortalLauncher.StopOwnedHost();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Maximized;
        try
        {
            StatusText.Text = "Starting the local web portal…";
            var url = await PortalLauncher.EnsureStartedAsync();
            Directory.CreateDirectory(AppPaths.WebView2UserData);
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2UserData);
            await PortalView.EnsureCoreWebView2Async(environment);
            PortalView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            PortalView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            PortalView.Source = new Uri(url.TrimEnd('/') + "/login");
            PortalView.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                }
            };
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not start Windows Admin: " + ex.Message;
        }
    }
}
