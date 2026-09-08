using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    public bool ToggleAllBlackBars()
        => EnhancedEdgeCoverService.Shared.ToggleAllVisibility();
}
