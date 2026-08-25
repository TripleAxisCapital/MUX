using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MUX.Core.Models;

namespace MUX.App.Windows;

public partial class AddMonitorWindow : Window
{
    public VirtualMonitorZone? Zone { get; private set; }

    public AddMonitorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SizeBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var diagonal) || diagonal < 5 || diagonal > 500)
        {
            ShowError("Enter a display size between 5 and 500 inches.");
            return;
        }

        var aspect = (AspectBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "16:9";
        var parts = aspect.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], out var aspectWidth) || !double.TryParse(parts[1], out var aspectHeight))
        {
            ShowError("Choose a valid aspect ratio.");
            return;
        }

        Zone = new VirtualMonitorZone
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? $"{diagonal:0.#}\" Monitor" : NameBox.Text.Trim(),
            DiagonalInches = diagonal,
            AspectWidth = aspectWidth,
            AspectHeight = aspectHeight
        };

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
