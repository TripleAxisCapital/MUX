using System.Windows;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private BlackBarsCommandBridge? _blackBarsCommandBridge;
    private bool _blackBarsStartupTogglePending;

    public bool ToggleAllBlackBars()
        => EnhancedEdgeCoverService.Shared.ToggleAllVisibility();

    internal void InitializeBlackBarsCommandBridge()
    {
        if (_blackBarsCommandBridge is not null)
        {
            return;
        }

        _blackBarsCommandBridge = BlackBarsCommandBridge.Attach(this, QueueBlackBarsToggleCommand);
        _blackBarsStartupTogglePending |= BlackBarsCommandBootstrap.ConsumeStartupRequest();

        Loaded += MainWindow_BlackBarsLoaded;
        Closed += MainWindow_BlackBarsClosed;
    }

    private void QueueBlackBarsToggleCommand()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(QueueBlackBarsToggleCommand));
            return;
        }

        _ = ToggleAllBlackBars();
    }

    private void MainWindow_BlackBarsLoaded(object sender, RoutedEventArgs e)
    {
        if (!_blackBarsStartupTogglePending)
        {
            return;
        }

        _blackBarsStartupTogglePending = false;
        _ = ToggleAllBlackBars();
    }

    private void MainWindow_BlackBarsClosed(object? sender, EventArgs e)
    {
        Loaded -= MainWindow_BlackBarsLoaded;
        Closed -= MainWindow_BlackBarsClosed;
        _blackBarsCommandBridge?.Dispose();
        _blackBarsCommandBridge = null;
    }
}
