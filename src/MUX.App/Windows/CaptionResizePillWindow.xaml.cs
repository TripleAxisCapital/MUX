using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MUX.App.Windows;

public sealed class CaptionResizeRequestEventArgs : EventArgs
{
    public CaptionResizeRequestEventArgs(
        IntPtr targetHwnd,
        double diagonalInches,
        double aspectWidth,
        double aspectHeight)
    {
        TargetHwnd = targetHwnd;
        DiagonalInches = diagonalInches;
        AspectWidth = aspectWidth;
        AspectHeight = aspectHeight;
    }

    public IntPtr TargetHwnd { get; }
    public double DiagonalInches { get; }
    public double AspectWidth { get; }
    public double AspectHeight { get; }
    public int ResultWidth { get; set; }
    public int ResultHeight { get; set; }
    public double ActualDiagonalInches { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
}

public partial class CaptionResizePillWindow : Window
{
    private readonly record struct AspectChoice(double Width, double Height, string Label, string MenuLabel);

    private sealed class FreeformSizePreferences
    {
        public double AspectWidth { get; set; } = 16;
        public double AspectHeight { get; set; } = 9;
    }

    private static readonly AspectChoice[] AspectPresets =
    {
        new(16, 9, "16:9", "16:9"),
        new(9, 16, "9:16", "9:16"),
        new(1, 1, "1:1", "Square (1:1)"),
        new(16, 10, "16:10", "16:10"),
        new(21, 9, "21:9", "21:9"),
        new(4, 3, "4:3", "4:3"),
        new(3, 2, "3:2", "3:2"),
        new(5, 4, "5:4", "5:4")
    };

    private const int MaxFavorites = 10;
    private const double BaseCollapsedWidth = 224;
    private const double ExpandedWidth = 354;
    private const double RatioEditorWidth = 250;
    private const double FavoriteButtonWidth = 52;
    private const double FavoriteGap = 6;
    private const double MinimumPillWidth = 176;
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(105);
    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(135);

    private readonly List<double> _favoriteSizes = new();
    private IntPtr _targetHwnd;
    private bool _expanded;
    private bool _ratioEditor;
    private bool _returnToSizeEditorAfterRatio;
    private bool _favoriteMenuOpen;
    private bool _aspectMenuOpen;
    private int? _editingFavoriteIndex;
    private long _visibilityAnimationVersion;
    private double _aspectWidth = 16;
    private double _aspectHeight = 9;
    private double _availableWidthDip = double.PositiveInfinity;
    private double? _currentPhysicalDiagonal;

    public CaptionResizePillWindow()
    {
        InitializeComponent();
        LoadPreferences();
        LoadFavorites();
        UpdateRatioButtons();
        RefreshFavoriteButtons(animate: false);
        UpdateFavoriteActionState();
    }

    public event EventHandler<CaptionResizeRequestEventArgs>? ResizeRequested;
    public event EventHandler? LayoutModeChanged;

    public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;
    public bool IsInteractionLocked => IsKeyboardFocusWithin || _favoriteMenuOpen || _aspectMenuOpen;
    public bool IsExpanded => _expanded;

    public void SetAvailableWidth(int targetWidthPx, uint dpi)
    {
        var effectiveDpi = Math.Max(96u, dpi);
        var widthDip = targetWidthPx * 96.0 / effectiveDpi;
        _availableWidthDip = Math.Max(MinimumPillWidth, widthDip - 8);
        SetPillWidth(CurrentDesiredWidth(), animate: false);
    }

    public void SetTarget(IntPtr hwnd, int width, int height, double? physicalDiagonalInches, string? displayName)
    {
        var targetChanged = hwnd != _targetHwnd;
        _targetHwnd = hwnd;
        UpdateTargetDimensions(width, height, physicalDiagonalInches, displayName, updateEditor: targetChanged || !IsKeyboardFocusWithin);

        if (targetChanged && _expanded)
        {
            SetCollapsed(animate: false);
        }
    }

    public void UpdateTargetDimensions(
        int width,
        int height,
        double? physicalDiagonalInches,
        string? displayName,
        bool updateEditor = true)
    {
        _currentPhysicalDiagonal = physicalDiagonalInches is > 0 ? physicalDiagonalInches : null;

        CurrentSizeText.Text = physicalDiagonalInches is > 0
            ? $"{physicalDiagonalInches.Value:0.#} in"
            : "— in";

        CurrentSizeText.ToolTip = physicalDiagonalInches is > 0
            ? $"{width:N0} × {height:N0} px · {physicalDiagonalInches.Value:0.##} in diagonal · {FormatAspect(_aspectWidth, _aspectHeight)}" +
              (string.IsNullOrWhiteSpace(displayName) ? string.Empty : $" · {displayName}")
            : "MUX needs a configured physical display size to calculate inches.";

        if (updateEditor && physicalDiagonalInches is > 0 && !_editingFavoriteIndex.HasValue && !_ratioEditor)
        {
            DiagonalBox.Text = physicalDiagonalInches.Value.ToString("0.##", CultureInfo.CurrentCulture);
        }
    }

    public void Reveal()
    {
        _visibilityAnimationVersion++;

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(Opacity, 1.0, RevealDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    public void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        var version = ++_visibilityAnimationVersion;
        var animation = new DoubleAnimation(Opacity, 0.0, DismissDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        animation.Completed += (_, _) =>
        {
            if (version != _visibilityAnimationVersion)
            {
                return;
            }

            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
        };

        BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void Collapse(bool animate = true)
    {
        SetCollapsed(animate);
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        ExitFavoriteEditMode();
        ShowSizeEditor(focusEditor: true, animate: true);
    }

    private void CurrentSize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExitFavoriteEditMode();
        ShowSizeEditor(focusEditor: true, animate: true);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (_ratioEditor && _returnToSizeEditorAfterRatio)
        {
            _returnToSizeEditorAfterRatio = false;
            ShowSizeEditor(focusEditor: false, animate: true);
            return;
        }

        SetCollapsed(animate: true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplyRequestedSize();
    }

    private void FavoriteAction_Click(object sender, RoutedEventArgs e)
    {
        SaveOrAddFavorite();
    }

    private void RatioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = button.ContextMenu ?? BuildAspectMenu(button);
        RefreshAspectMenuChecks(menu);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private ContextMenu BuildAspectMenu(Button owner)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, typeof(ContextMenu));

        foreach (var preset in AspectPresets)
        {
            var item = new MenuItem
            {
                Header = preset.MenuLabel,
                Tag = preset,
                IsCheckable = true,
                IsChecked = AspectMatches(_aspectWidth, _aspectHeight, preset.Width, preset.Height)
            };
            item.SetResourceReference(FrameworkElement.StyleProperty, typeof(MenuItem));
            item.Click += AspectPreset_Click;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Custom…" };
        custom.SetResourceReference(FrameworkElement.StyleProperty, typeof(MenuItem));
        custom.Click += AspectCustom_Click;
        menu.Items.Add(custom);

        menu.Opened += (_, _) => _aspectMenuOpen = true;
        menu.Closed += (_, _) => _aspectMenuOpen = false;
        owner.ContextMenu = menu;
        return menu;
    }

    private void RefreshAspectMenuChecks(ContextMenu menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is AspectChoice choice)
            {
                item.IsChecked = AspectMatches(_aspectWidth, _aspectHeight, choice.Width, choice.Height);
            }
        }
    }

    private void AspectPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not AspectChoice choice)
        {
            return;
        }

        var applyImmediately = !_expanded;
        SetAspectRatio(choice.Width, choice.Height, save: true, applyToCurrent: applyImmediately);
    }

    private void AspectCustom_Click(object sender, RoutedEventArgs e)
    {
        _returnToSizeEditorAfterRatio = _expanded && !_ratioEditor;
        ShowRatioEditor(focusEditor: true, animate: true);
    }

    private void SaveRatio_Click(object sender, RoutedEventArgs e)
    {
        SaveCustomRatio();
    }

    private void CustomRatioBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveCustomRatio();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse_Click(sender, e);
        }
    }

    private void SaveCustomRatio()
    {
        if (!TryParseAspect(CustomRatioBox.Text, out var width, out var height))
        {
            CustomRatioBox.ToolTip = "Enter a ratio such as 16:9, 9:16, 1:1, or 2.39:1.";
            CustomRatioBox.SelectAll();
            CustomRatioBox.Focus();
            return;
        }

        var returnToSize = _returnToSizeEditorAfterRatio;
        SetAspectRatio(width, height, save: true, applyToCurrent: !returnToSize);
        _returnToSizeEditorAfterRatio = false;

        if (returnToSize)
        {
            ShowSizeEditor(focusEditor: false, animate: true);
        }
        else
        {
            SetCollapsed(animate: true);
        }
    }

    private void SetAspectRatio(double width, double height, bool save, bool applyToCurrent)
    {
        NormalizeAspect(ref width, ref height);
        _aspectWidth = width;
        _aspectHeight = height;
        UpdateRatioButtons();

        if (save)
        {
            SavePreferences();
        }

        if (!applyToCurrent || _currentPhysicalDiagonal is not > 0 || _targetHwnd == IntPtr.Zero)
        {
            return;
        }

        if (TryResizeTarget(_currentPhysicalDiagonal.Value, out var args))
        {
            UpdateTargetDimensions(
                args.ResultWidth,
                args.ResultHeight,
                args.ActualDiagonalInches > 0 ? args.ActualDiagonalInches : _currentPhysicalDiagonal,
                args.DisplayName,
                updateEditor: false);
        }
        else
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? "Windows did not allow MUX to apply that aspect ratio."
                : args.ErrorMessage;
            RatioButton.ToolTip = message;
            EditorRatioButton.ToolTip = message;
        }
    }

    private void UpdateRatioButtons()
    {
        var label = FormatAspect(_aspectWidth, _aspectHeight);
        RatioButton.Content = label;
        EditorRatioButton.Content = label;
        RatioButton.ToolTip = $"Aspect ratio: {label}";
        EditorRatioButton.ToolTip = $"Aspect ratio: {label}";
    }

    private void DiagonalBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_editingFavoriteIndex.HasValue)
            {
                SaveOrAddFavorite();
            }
            else
            {
                ApplyRequestedSize();
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetCollapsed(animate: true);
        }
    }

    private void ApplyRequestedSize()
    {
        if (!TryGetEditorDiagonal(out var diagonal))
        {
            return;
        }

        if (!TryResizeTarget(diagonal, out var args))
        {
            ShowEditorError(args.ErrorMessage);
            return;
        }

        UpdateTargetDimensions(
            args.ResultWidth,
            args.ResultHeight,
            args.ActualDiagonalInches > 0 ? args.ActualDiagonalInches : diagonal,
            args.DisplayName);
        SetCollapsed(animate: true);
    }

    private void SaveOrAddFavorite()
    {
        if (!TryGetEditorDiagonal(out var diagonal))
        {
            return;
        }

        diagonal = NormalizeFavorite(diagonal);

        if (_editingFavoriteIndex is int editingIndex)
        {
            if (editingIndex < 0 || editingIndex >= _favoriteSizes.Count)
            {
                ExitFavoriteEditMode();
                ShowEditorError("That favorite is no longer available.");
                return;
            }

            if (FindFavoriteIndex(diagonal, editingIndex) >= 0)
            {
                ShowEditorError("That size is already in your favorites.");
                return;
            }

            _favoriteSizes[editingIndex] = diagonal;
            SaveFavorites();
            ExitFavoriteEditMode();
            RefreshFavoriteButtons(animate: false);
            SetCollapsed(animate: true);
            return;
        }

        if (_favoriteSizes.Count >= MaxFavorites)
        {
            ShowEditorError("You can save up to 10 favorite sizes. Right-click a favorite to edit or delete it.");
            return;
        }

        if (FindFavoriteIndex(diagonal, exceptIndex: null) >= 0)
        {
            ShowEditorError("That size is already in your favorites.");
            return;
        }

        _favoriteSizes.Add(diagonal);
        SaveFavorites();
        RefreshFavoriteButtons(animate: false);
        SetCollapsed(animate: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => FavoritesScroll.ScrollToEnd()));
    }

    private bool TryGetEditorDiagonal(out double diagonal)
    {
        if (!TryParseDiagonal(DiagonalBox.Text, out diagonal) || !IsValidDiagonal(diagonal))
        {
            ShowEditorError("Enter a physical diagonal from 1 to 500 inches.");
            return false;
        }

        return true;
    }

    private bool TryResizeTarget(double diagonal, out CaptionResizeRequestEventArgs args)
    {
        args = new CaptionResizeRequestEventArgs(_targetHwnd, diagonal, _aspectWidth, _aspectHeight);
        if (_targetHwnd == IntPtr.Zero)
        {
            args.ErrorMessage = "Move the pointer back to the window you want to resize.";
            return false;
        }

        ResizeRequested?.Invoke(this, args);
        if (!args.Succeeded && string.IsNullOrWhiteSpace(args.ErrorMessage))
        {
            args.ErrorMessage = "Windows did not allow MUX to resize this window.";
        }

        return args.Succeeded;
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int index || index < 0 || index >= _favoriteSizes.Count)
        {
            return;
        }

        var diagonal = _favoriteSizes[index];
        if (!TryResizeTarget(diagonal, out var args))
        {
            ExitFavoriteEditMode();
            DiagonalBox.Text = diagonal.ToString("0.##", CultureInfo.CurrentCulture);
            ShowSizeEditor(focusEditor: true, animate: true);
            ShowEditorError(args.ErrorMessage);
            return;
        }

        UpdateTargetDimensions(
            args.ResultWidth,
            args.ResultHeight,
            args.ActualDiagonalInches > 0 ? args.ActualDiagonalInches : diagonal,
            args.DisplayName);
    }

    private void FavoriteEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not int index || index < 0 || index >= _favoriteSizes.Count)
        {
            return;
        }

        _favoriteMenuOpen = false;
        _editingFavoriteIndex = index;
        DiagonalBox.Text = _favoriteSizes[index].ToString("0.##", CultureInfo.CurrentCulture);
        UpdateFavoriteActionState();
        ShowSizeEditor(focusEditor: true, animate: true);
    }

    private void FavoriteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not int index || index < 0 || index >= _favoriteSizes.Count)
        {
            return;
        }

        _favoriteMenuOpen = false;
        _favoriteSizes.RemoveAt(index);

        if (_editingFavoriteIndex == index)
        {
            ExitFavoriteEditMode();
        }
        else if (_editingFavoriteIndex is int editingIndex && editingIndex > index)
        {
            _editingFavoriteIndex = editingIndex - 1;
        }

        SaveFavorites();
        RefreshFavoriteButtons(animate: true);
    }

    private void RefreshFavoriteButtons(bool animate)
    {
        FavoritesPanel.Children.Clear();

        for (var index = 0; index < _favoriteSizes.Count; index++)
        {
            var value = _favoriteSizes[index];
            var button = new Button
            {
                Width = FavoriteButtonWidth,
                Height = 34,
                Margin = new Thickness(0, 0, FavoriteGap, 0),
                Content = FormatFavorite(value),
                Tag = index,
                ToolTip = $"Resize to {FormatFavorite(value)} · {FormatAspect(_aspectWidth, _aspectHeight)} · right-click to edit or delete"
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "FavoritePillButton");
            button.Click += Favorite_Click;

            var editItem = new MenuItem
            {
                Header = "Edit value",
                Tag = index
            };
            editItem.SetResourceReference(FrameworkElement.StyleProperty, typeof(MenuItem));
            editItem.Click += FavoriteEdit_Click;

            var deleteItem = new MenuItem
            {
                Header = "Delete favorite",
                Tag = index
            };
            deleteItem.SetResourceReference(FrameworkElement.StyleProperty, typeof(MenuItem));
            deleteItem.Click += FavoriteDelete_Click;

            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, typeof(ContextMenu));
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
            menu.Opened += (_, _) => _favoriteMenuOpen = true;
            menu.Closed += (_, _) => _favoriteMenuOpen = false;
            button.ContextMenu = menu;

            FavoritesPanel.Children.Add(button);
        }

        UpdateFavoriteActionState();

        if (!_expanded)
        {
            SetPillWidth(CalculateCollapsedWidth(), animate && IsVisible);
        }
    }

    private void FavoritesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FavoritesScroll.ScrollableWidth <= 0)
        {
            return;
        }

        FavoritesScroll.ScrollToHorizontalOffset(
            Math.Clamp(FavoritesScroll.HorizontalOffset - e.Delta * 0.45, 0, FavoritesScroll.ScrollableWidth));
        e.Handled = true;
    }

    private void UpdateFavoriteActionState()
    {
        var editing = _editingFavoriteIndex.HasValue;
        EditorTitleText.Text = editing ? "Favorite" : "Diagonal";
        FavoriteActionButton.Content = editing ? "✓" : "+";
        FavoriteActionButton.FontSize = editing ? 14 : 17;
        FavoriteActionButton.ToolTip = editing
            ? "Save this favorite"
            : _favoriteSizes.Count >= MaxFavorites
                ? "Maximum of 10 favorites reached"
                : "Add this size to favorites";
        FavoriteActionButton.IsEnabled = editing || _favoriteSizes.Count < MaxFavorites;
    }

    private void ExitFavoriteEditMode()
    {
        if (!_editingFavoriteIndex.HasValue)
        {
            UpdateFavoriteActionState();
            return;
        }

        _editingFavoriteIndex = null;
        UpdateFavoriteActionState();
    }

    private void ShowEditorError(string? message)
    {
        DiagonalBox.ToolTip = string.IsNullOrWhiteSpace(message)
            ? "MUX could not complete that action."
            : message;
        DiagonalBox.SelectAll();
        DiagonalBox.Focus();
        Keyboard.Focus(DiagonalBox);
    }

    private int FindFavoriteIndex(double value, int? exceptIndex)
    {
        for (var index = 0; index < _favoriteSizes.Count; index++)
        {
            if (exceptIndex == index)
            {
                continue;
            }

            if (Math.Abs(_favoriteSizes[index] - value) < 0.005)
            {
                return index;
            }
        }

        return -1;
    }

    private void LoadFavorites()
    {
        try
        {
            var path = GetFavoritesPath();
            if (!File.Exists(path))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<List<double>>(File.ReadAllText(path));
            if (saved is null)
            {
                return;
            }

            foreach (var raw in saved)
            {
                if (_favoriteSizes.Count >= MaxFavorites || !IsValidDiagonal(raw))
                {
                    continue;
                }

                var value = NormalizeFavorite(raw);
                if (FindFavoriteIndex(value, exceptIndex: null) < 0)
                {
                    _favoriteSizes.Add(value);
                }
            }
        }
        catch
        {
            // Favorites are optional convenience state and must never prevent MUX from opening.
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var path = GetFavoritesPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(_favoriteSizes, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A failed convenience-state write must never interrupt window resizing.
        }
    }

    private void LoadPreferences()
    {
        try
        {
            var path = GetPreferencesPath();
            if (!File.Exists(path))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<FreeformSizePreferences>(File.ReadAllText(path));
            if (saved is null || !IsValidAspect(saved.AspectWidth, saved.AspectHeight))
            {
                return;
            }

            var width = saved.AspectWidth;
            var height = saved.AspectHeight;
            NormalizeAspect(ref width, ref height);
            _aspectWidth = width;
            _aspectHeight = height;
        }
        catch
        {
            _aspectWidth = 16;
            _aspectHeight = 9;
        }
    }

    private void SavePreferences()
    {
        try
        {
            var path = GetPreferencesPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var preferences = new FreeformSizePreferences
            {
                AspectWidth = _aspectWidth,
                AspectHeight = _aspectHeight
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Aspect-ratio preferences are convenience state and must never interrupt MUX.
        }
    }

    private static string GetFavoritesPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "freeform-size-favorites.json");
    }

    private static string GetPreferencesPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "freeform-size-settings.json");
    }

    private static bool IsValidDiagonal(double diagonal)
    {
        return double.IsFinite(diagonal) && diagonal is >= 1 and <= 500;
    }

    private static bool IsValidAspect(double width, double height)
    {
        return double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0 && width <= 10000 && height <= 10000;
    }

    private static double NormalizeFavorite(double diagonal)
    {
        return Math.Round(diagonal, 2, MidpointRounding.AwayFromZero);
    }

    private static string FormatFavorite(double diagonal)
    {
        return $"{diagonal:0.##} in";
    }

    private static bool TryParseDiagonal(string text, out double diagonal)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out diagonal) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out diagonal);
    }

    private static bool TryParseAspect(string text, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim()
            .Replace('×', ':')
            .Replace('x', ':')
            .Replace('X', ':')
            .Replace('/', ':');
        var parts = normalized.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            if (!TryParsePositive(parts[0], out width))
            {
                return false;
            }
            height = 1;
        }
        else if (parts.Length == 2)
        {
            if (!TryParsePositive(parts[0], out width) || !TryParsePositive(parts[1], out height))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        return IsValidAspect(width, height);
    }

    private static bool TryParsePositive(string text, out double value)
    {
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
               double.IsFinite(value) && value > 0;
    }

    private static void NormalizeAspect(ref double width, ref double height)
    {
        foreach (var preset in AspectPresets)
        {
            if (AspectMatches(width, height, preset.Width, preset.Height))
            {
                width = preset.Width;
                height = preset.Height;
                return;
            }
        }

        var ratio = width / height;
        width = Math.Round(ratio, 4, MidpointRounding.AwayFromZero);
        height = 1;
    }

    private static bool AspectMatches(double width, double height, double otherWidth, double otherHeight)
    {
        if (!IsValidAspect(width, height) || !IsValidAspect(otherWidth, otherHeight))
        {
            return false;
        }

        return Math.Abs(width / height - otherWidth / otherHeight) < 0.0005;
    }

    private static string FormatAspect(double width, double height)
    {
        foreach (var preset in AspectPresets)
        {
            if (AspectMatches(width, height, preset.Width, preset.Height))
            {
                return preset.Label;
            }
        }

        return $"{width / height:0.##}:1";
    }

    private double CalculateCollapsedWidth()
    {
        return BaseCollapsedWidth + _favoriteSizes.Count * (FavoriteButtonWidth + FavoriteGap);
    }

    private double CurrentDesiredWidth()
    {
        if (_ratioEditor)
        {
            return RatioEditorWidth;
        }

        return _expanded ? ExpandedWidth : CalculateCollapsedWidth();
    }

    private void ShowSizeEditor(bool focusEditor, bool animate)
    {
        _expanded = true;
        _ratioEditor = false;
        CollapsedPanel.Visibility = Visibility.Collapsed;
        ExpandedScroll.Visibility = Visibility.Visible;
        RatioEditorPanel.Visibility = Visibility.Collapsed;
        SetPillWidth(ExpandedWidth, animate);

        if (focusEditor)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Activate();
                DiagonalBox.Focus();
                DiagonalBox.SelectAll();
                Keyboard.Focus(DiagonalBox);
            }));
        }
    }

    private void ShowRatioEditor(bool focusEditor, bool animate)
    {
        _expanded = true;
        _ratioEditor = true;
        CollapsedPanel.Visibility = Visibility.Collapsed;
        ExpandedScroll.Visibility = Visibility.Collapsed;
        RatioEditorPanel.Visibility = Visibility.Visible;
        CustomRatioBox.Text = FormatAspect(_aspectWidth, _aspectHeight);
        SetPillWidth(RatioEditorWidth, animate);

        if (focusEditor)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Activate();
                CustomRatioBox.Focus();
                CustomRatioBox.SelectAll();
                Keyboard.Focus(CustomRatioBox);
            }));
        }
    }

    private void SetCollapsed(bool animate)
    {
        ExitFavoriteEditMode();
        _expanded = false;
        _ratioEditor = false;
        _returnToSizeEditorAfterRatio = false;
        CollapsedPanel.Visibility = Visibility.Visible;
        ExpandedScroll.Visibility = Visibility.Collapsed;
        RatioEditorPanel.Visibility = Visibility.Collapsed;
        SetPillWidth(CalculateCollapsedWidth(), animate);
    }

    private void SetPillWidth(double targetWidth, bool animate)
    {
        var constrainedWidth = Math.Max(MinimumPillWidth, Math.Min(targetWidth, _availableWidthDip));

        if (!animate || !IsVisible)
        {
            BeginAnimation(WidthProperty, null);
            Width = constrainedWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var start = ActualWidth > 0 ? ActualWidth : Width;
        if (Math.Abs(start - constrainedWidth) < 0.5)
        {
            BeginAnimation(WidthProperty, null);
            Width = constrainedWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Width = constrainedWidth;
        var animation = new DoubleAnimation(start, constrainedWidth, ResizeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            Width = constrainedWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        LayoutModeChanged?.Invoke(this, EventArgs.Empty);
    }
}
