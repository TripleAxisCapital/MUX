using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MUX.App.Services;
using MUX.Core.Models;

namespace MUX.App.Windows;

public partial class ShortcutSettingsWindow : Window
{
    private ShortcutSettings _working;
    private Button? _captureButton;
    private ShortcutBinding? _captureBinding;

    public ShortcutSettingsWindow(ShortcutSettings settings)
    {
        InitializeComponent();
        _working = StateStore.DeepClone(settings ?? new ShortcutSettings());
        RefreshButtons();
    }

    public ShortcutSettings Shortcuts { get; private set; } = new();

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => BeginCapture(ToggleButton, _working.ToggleMaximize);
    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => BeginCapture(FullscreenButton, _working.ToggleFullscreen);
    private void PreviousButton_Click(object sender, RoutedEventArgs e) => BeginCapture(PreviousButton, _working.PreviousMonitor);
    private void NextButton_Click(object sender, RoutedEventArgs e) => BeginCapture(NextButton, _working.NextMonitor);
    private void EditButton_Click(object sender, RoutedEventArgs e) => BeginCapture(EditButton, _working.EditLayout);

    private void BeginCapture(Button button, ShortcutBinding binding)
    {
        _captureButton = button;
        _captureBinding = binding;
        ErrorText.Visibility = Visibility.Collapsed;
        RefreshButtons();
        button.Content = "Press shortcut…";
        button.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureButton is null || _captureBinding is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key == Key.Escape)
        {
            _captureButton = null;
            _captureBinding = null;
            RefreshButtons();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            ShowError("Include at least one modifier key — Ctrl, Alt, Shift, or Windows.");
            return;
        }

        _captureBinding.Control = modifiers.HasFlag(ModifierKeys.Control);
        _captureBinding.Alt = modifiers.HasFlag(ModifierKeys.Alt);
        _captureBinding.Shift = modifiers.HasFlag(ModifierKeys.Shift);
        _captureBinding.Windows = modifiers.HasFlag(ModifierKeys.Windows);
        _captureBinding.Key = key.ToString();

        _captureButton = null;
        _captureBinding = null;
        ErrorText.Visibility = Visibility.Collapsed;
        RefreshButtons();
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        _captureButton = null;
        _captureBinding = null;
        _working = new ShortcutSettings();
        ErrorText.Visibility = Visibility.Collapsed;
        RefreshButtons();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_captureButton is not null)
        {
            ShowError("Finish capturing the current shortcut first, or press Esc to cancel it.");
            return;
        }

        var bindings = new[]
        {
            _working.ToggleMaximize,
            _working.ToggleFullscreen,
            _working.PreviousMonitor,
            _working.NextMonitor,
            _working.EditLayout
        };

        if (bindings.Any(binding => string.IsNullOrWhiteSpace(binding.Key) ||
                                    !(binding.Control || binding.Alt || binding.Shift || binding.Windows)))
        {
            ShowError("Every shortcut needs a key and at least one modifier.");
            return;
        }

        var duplicate = bindings.GroupBy(Signature, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            ShowError("Each MUX action needs a unique shortcut.");
            return;
        }

        Shortcuts = StateStore.DeepClone(_working);
        DialogResult = true;
    }

    private void RefreshButtons()
    {
        ToggleButton.Content = Format(_working.ToggleMaximize);
        FullscreenButton.Content = Format(_working.ToggleFullscreen);
        PreviousButton.Content = Format(_working.PreviousMonitor);
        NextButton.Content = Format(_working.NextMonitor);
        EditButton.Content = Format(_working.EditLayout);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string Signature(ShortcutBinding binding) =>
        $"{binding.Control}:{binding.Alt}:{binding.Shift}:{binding.Windows}:{binding.Key}";

    public static string Format(ShortcutBinding binding)
    {
        var parts = new List<string>();
        if (binding.Control) parts.Add("Ctrl");
        if (binding.Alt) parts.Add("Alt");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Windows) parts.Add("Win");
        parts.Add(FriendlyKey(binding.Key));
        return string.Join(" + ", parts);
    }

    private static string FriendlyKey(string key) => key switch
    {
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Return" => "Enter",
        "OemPlus" => "+",
        "OemMinus" => "-",
        _ => key
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;
}
