using System.Windows;

namespace MUX.App.Windows;

public partial class InputWindow : Window
{
    public string Value => ValueBox.Text.Trim();

    public InputWindow(string title, string subtitle, string initialValue = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            DialogResult = true;
        }
    }
}
