using System.Windows;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private BlackBarsCommandBridge? _blackBarsCommandBridge;
    private bool _blackBarsStartupTogglePending;
    private bool _blackBarsCallbacksInstalled;

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

        if (!_blackBarsCallbacksInstalled)
        {
            _blackBarsCallbacksInstalled = true;
            WorkspaceReady += MainWindow_BlackBarsReady;
            Closed += MainWindow_BlackBarsClosed;
        }
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

    private void MainWindow_BlackBarsReady(object? sender, EventArgs e)
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
        WorkspaceReady -= MainWindow_BlackBarsReady;
        Closed -= MainWindow_BlackBarsClosed;
        _blackBarsCommandBridge?.Dispose();
        _blackBarsCommandBridge = null;
    }
}
