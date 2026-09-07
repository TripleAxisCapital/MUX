using MUX.App.Services;

namespace MUX.Virtual.App;

public partial class MainWindow
{
    private CaptionResizePillService? _physicalResizePillService;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_physicalResizePillService is not null)
        {
            return;
        }

        // Reuse Standard's calibrated physical-inch sizing UI with this edition's live shared state.
        _physicalResizePillService = new CaptionResizePillService(
            () => new DisplaySizingSnapshot(_state.Displays, _state.ActiveDisplayDeviceName));
        _physicalResizePillService.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _physicalResizePillService?.Dispose();
        _physicalResizePillService = null;
        base.OnClosed(e);
    }
}
