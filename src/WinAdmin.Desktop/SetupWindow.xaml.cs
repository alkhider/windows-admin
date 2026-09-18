using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using WinAdmin.Core.Models;
using WinAdmin.Core.Services;

namespace WinAdmin.Desktop;

public partial class SetupWindow : Window
{
    private MachineResetResult _reset;
    private List<SupportLibrary> _libraries;

    public SetupWindow(MachineResetResult reset, IReadOnlyList<SupportLibrary> libraries)
    {
        InitializeComponent();
        _reset = reset;
        _libraries = libraries.ToList();
        Render();
        StyleButtons();
    }

    private void Render()
    {
        var missingRequired = _libraries.Where(l => l.Required && !l.Installed).ToList();
        var missingOptional = _libraries.Where(l => !l.Required && !l.Installed).ToList();
        if (_reset.DataRemoved)
        {
            IntroText.Text = _reset.Message;
        }
        else if (missingRequired.Count > 0)
        {
            IntroText.Text = "Required libraries were not found. Install them with Get this, then Check again. You can still choose Try to run.";
        }
        else if (missingOptional.Count > 0)
        {
            IntroText.Text = "Required libraries are ready. Optional items can wait. Continue, or Try to run now.";
        }
        else
        {
            IntroText.Text = string.IsNullOrWhiteSpace(_reset.Message)
                ? "All support libraries are ready."
                : _reset.Message + " All support libraries are ready.";
        }

        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = _libraries;
        ContinueButton.IsEnabled = missingRequired.Count == 0;
        TryRunButton.IsEnabled = true;
    }

    private void StyleButtons()
    {
        var border = (Color)ColorConverter.ConvertFromString("#3a3550")!;
        foreach (var button in new[] { RecheckButton, ExitButton, ContinueButton, TryRunButton })
        {
            button.Background = Brushes.Transparent;
            button.Foreground = Brushes.White;
            button.BorderBrush = new SolidColorBrush(border);
        }

        TryRunButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8c98a")!);
    }

    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        _libraries = Probe().ToList();
        Render();
    }

    private void GetThis_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SupportLibrary library } || string.IsNullOrWhiteSpace(library.DownloadUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(library.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Windows Admin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_libraries.Any(l => l.Required && !l.Installed))
        {
            MessageBox.Show(
                this,
                "Install the required libraries, then choose Check again. Or choose Try to run.",
                "Windows Admin",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void TryRun_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static IReadOnlyList<SupportLibrary> Probe()
    {
        string? webView2 = null;
        try
        {
            webView2 = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            // registry/file checks still run inside SupportLibraries
        }

        return SupportLibraries.Check(webView2);
    }
}
