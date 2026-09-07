using System.Windows;
using System.Windows.Controls;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private CaptionResizePillService? _captionResizePillService;
    private CheckBox? _predefinedAreasCheck;
    private bool _freeformControlsInitialized;

    public event EventHandler? PredefinedAreasEnabledChanged;

    public bool PredefinedAreasEnabled => _state.Enabled;

    public void InitializeFreeformControls()
    {
        if (_freeformControlsInitialized)
        {
            return;
        }

        _freeformControlsInitialized = true;
        InstallPredefinedAreasControl();

        EngineToggleButton.ToolTip = "Turn predefined MUX window areas on or off. Exact freeform resizing stays available.";
        EngineToggleButton.Click += EngineToggleButton_FreeformStateChanged;
        Loaded += MainWindow_FreeformLoaded;
        Closed += MainWindow_FreeformClosed;

        _captionResizePillService = new CaptionResizePillService();
        _captionResizePillService.Start();
    }

    public async Task SetPredefinedAreasEnabledAsync(bool enabled)
    {
        if (_state.Enabled != enabled)
        {
            _state.Enabled = enabled;
            RefreshEngineVisual();
            ConfigureWindowManager();
            await SaveStateAsync();
        }

        SyncPredefinedAreasUi();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InstallPredefinedAreasControl()
    {
        if (StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        _predefinedAreasCheck = new CheckBox
        {
            Content = "Use predefined window areas",
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "Disable this for freeform Windows sizing without deleting your saved MUX layouts."
        };
        _predefinedAreasCheck.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
        _predefinedAreasCheck.Click += PredefinedAreasCheck_Click;

        var snapIndex = behaviorPanel.Children.IndexOf(SnapCheck);
        behaviorPanel.Children.Insert(Math.Max(0, snapIndex), _predefinedAreasCheck);
    }

    private void MainWindow_FreeformLoaded(object sender, RoutedEventArgs e)
    {
        SyncPredefinedAreasUi();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MainWindow_FreeformClosed(object? sender, EventArgs e)
    {
        _captionResizePillService?.Dispose();
        _captionResizePillService = null;
    }

    private void EngineToggleButton_FreeformStateChanged(object sender, RoutedEventArgs e)
    {
        SyncPredefinedAreasUi();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void PredefinedAreasCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_predefinedAreasCheck is null)
        {
            return;
        }

        await SetPredefinedAreasEnabledAsync(_predefinedAreasCheck.IsChecked == true);
    }

    private void SyncPredefinedAreasUi()
    {
        if (_predefinedAreasCheck is not null)
        {
            _predefinedAreasCheck.IsChecked = _state.Enabled;
        }

        SnapCheck.IsEnabled = _state.Enabled;
        if (_snapModeBox is not null) _snapModeBox.IsEnabled = _state.Enabled;
        if (_outlineCheck is not null) _outlineCheck.IsEnabled = _state.Enabled;
        if (_outlineThicknessBox is not null) _outlineThicknessBox.IsEnabled = _state.Enabled;

        EngineSubtitle.Text = _state.Enabled ? "Predefined areas on" : "Freeform mode";
    }
}
