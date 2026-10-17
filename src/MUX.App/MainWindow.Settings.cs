using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MUX.App;

public partial class MainWindow
{
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = SettingsView.Visibility != Visibility.Visible;
        SettingsView.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        MainWorkspaceView.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        MainWorkspaceView.Visibility = Visibility.Visible;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void StreamDeckCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string launcherName })
        {
            return;
        }

        var launcherPath = Path.Combine(AppContext.BaseDirectory, launcherName);
        try
        {
            Clipboard.SetText(launcherPath);
            StreamDeckSetupStatusText.Text = File.Exists(launcherPath)
                ? $"Copied {launcherName}. Paste it into Stream Deck → System → Open."
                : $"Copied the expected path for {launcherName}. The helper appears after the managed Standard build is installed.";
        }
        catch (Exception exception)
        {
            StreamDeckSetupStatusText.Text = $"Could not copy the launcher path: {exception.Message}";
        }
    }

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\"",
                UseShellExecute = true
            });
            StreamDeckSetupStatusText.Text = "Opened the MUX installation folder.";
        }
        catch (Exception exception)
        {
            StreamDeckSetupStatusText.Text = $"Could not open the MUX folder: {exception.Message}";
        }
    }
}
