using System.Windows;
using System.Windows.Media;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private MuxUpdateService? _updateService;

    internal void InitializeUpdates(MuxUpdateService updateService)
    {
        if (_updateService is not null)
        {
            return;
        }

        _updateService = updateService;
        _updateService.StatusChanged += UpdateService_StatusChanged;

        UpdateVersionText.Text = FormatVersion(_updateService.CurrentVersion);
        UpdateStatusCapsule.ToolTip = _updateService.IsManagedInstall
            ? "MUX checks the managed GitHub release channel automatically."
            : "Install the current MUX Setup once to move this copy onto the managed update channel.";

        Loaded += MainWindow_UpdatesLoaded;
        Closed += MainWindow_UpdatesClosed;
    }

    private async void MainWindow_UpdatesLoaded(object sender, RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            return;
        }

        try
        {
            // Let the main workspace render first so update I/O never competes with startup.
            await Task.Delay(650);
            await _updateService.CheckAsync();
        }
        catch (Exception exception)
        {
            ApplyUpdateStatus(new MuxUpdateStatus(
                "check_failed",
                "Update check failed · " + exception.GetBaseException().Message,
                0,
                _updateService.CurrentVersion));
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            return;
        }

        if (!_updateService.IsManagedInstall)
        {
            ApplyUpdateStatus(new MuxUpdateStatus(
                "setup_required",
                "Install the current managed MUX Setup once to enable one-click updates.",
                0,
                _updateService.CurrentVersion));

            MessageBox.Show(
                this,
                "This copy of MUX predates the managed updater. Uninstall the old MUX application, then run the current MUX Setup once. Your layouts and settings in LocalAppData are preserved. After that, all future MUX updates can be installed directly from this control.",
                "One-time updater migration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            await _updateService.DownloadAndInstallAsync();
        }
        catch (Exception exception)
        {
            ApplyUpdateStatus(new MuxUpdateStatus(
                "check_failed",
                "Update failed · " + exception.GetBaseException().Message,
                0,
                _updateService.CurrentVersion));

            MessageBox.Show(
                this,
                exception.GetBaseException().Message,
                "MUX update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        finally
        {
            if (IsLoaded)
            {
                UpdateButton.IsEnabled = true;
            }
        }
    }

    private void UpdateService_StatusChanged(object? sender, MuxUpdateStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyUpdateStatus(status)));
            return;
        }

        ApplyUpdateStatus(status);
    }

    private void ApplyUpdateStatus(MuxUpdateStatus status)
    {
        UpdateStatusCapsule.ToolTip = status.Message;
        UpdateVersionText.Text = FormatVersion(status.Version ?? _updateService?.CurrentVersion ?? "dev");
        UpdateProgressBar.Value = status.Progress;
        UpdateProgressBar.Visibility = status.State is "checking" or "downloading" or "installing"
            ? Visibility.Visible
            : Visibility.Collapsed;

        switch (status.State)
        {
            case "checking":
                UpdateStatusText.Text = "Checking";
                UpdateStatusDot.Fill = BrushFrom(116, 166, 207);
                UpdateButton.Content = "Checking";
                UpdateButton.IsEnabled = false;
                break;

            case "available":
                UpdateStatusText.Text = "Update ready";
                UpdateStatusDot.Fill = BrushFrom(231, 196, 123);
                UpdateButton.Content = "Install";
                UpdateButton.IsEnabled = true;
                break;

            case "downloading":
                UpdateStatusText.Text = "Downloading";
                UpdateStatusDot.Fill = BrushFrom(116, 166, 207);
                UpdateButton.Content = "Updating";
                UpdateButton.IsEnabled = false;
                break;

            case "installing":
                UpdateStatusText.Text = "Restarting";
                UpdateStatusDot.Fill = BrushFrom(127, 224, 170);
                UpdateButton.Content = "Applying";
                UpdateButton.IsEnabled = false;
                break;

            case "current":
                UpdateStatusText.Text = "Up to date";
                UpdateStatusDot.Fill = BrushFrom(127, 224, 170);
                UpdateButton.Content = "Check";
                UpdateButton.IsEnabled = true;
                break;

            case "setup_required":
                UpdateStatusText.Text = "Setup required";
                UpdateStatusDot.Fill = BrushFrom(231, 196, 123);
                UpdateButton.Content = "Migrate";
                UpdateButton.IsEnabled = true;
                break;

            default:
                UpdateStatusText.Text = "Update unavailable";
                UpdateStatusDot.Fill = BrushFrom(255, 125, 136);
                UpdateButton.Content = "Retry";
                UpdateButton.IsEnabled = true;
                break;
        }
    }

    private void MainWindow_UpdatesClosed(object? sender, EventArgs e)
    {
        Loaded -= MainWindow_UpdatesLoaded;
        Closed -= MainWindow_UpdatesClosed;

        if (_updateService is not null)
        {
            _updateService.StatusChanged -= UpdateService_StatusChanged;
            _updateService = null;
        }
    }

    private static string FormatVersion(string value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("dev", StringComparison.OrdinalIgnoreCase)
            ? "development"
            : value.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? value
                : "v" + value;

    private static SolidColorBrush BrushFrom(byte red, byte green, byte blue)
        => new(Color.FromRgb(red, green, blue));
}
