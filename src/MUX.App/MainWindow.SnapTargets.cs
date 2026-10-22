using System.Windows;
using System.Windows.Media;
using MUX.App.Services;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class MainWindow
{
    private async void SnapColorButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ZoneDragPreviewService.ParseColor(_state.SnapHighlightColor);
        using var chooser = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = false,
            Color = System.Drawing.Color.FromArgb(selected.R, selected.G, selected.B)
        };

        if (chooser.ShowDialog() != Forms.DialogResult.OK) return;

        var color = chooser.Color;
        _state.SnapHighlightColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SnapColorPreview.Background = new SolidColorBrush(
            Color.FromRgb(color.R, color.G, color.B));
        _windowManager.SetDragHighlightColor(_state.SnapHighlightColor);
        await SaveStateAsync();
    }
}
