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
    public CaptionResizeRequestEventArgs(IntPtr targetHwnd, double diagonalInches)
    {
        TargetHwnd = targetHwnd;
        DiagonalInches = diagonalInches;
    }

    public IntPtr TargetHwnd { get; }
    public double DiagonalInches { get; }
    public int ResultWidth { get; set; }
    public int ResultHeight { get; set; }
    public double ActualDiagonalInches { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
}

public partial class CaptionResizePillWindow : Window
{
    private const int MaxFavorites = 10;
    private const double BaseCollapsedWidth = 182;
    private const double ExpandedWidth = 320;
    private const double FavoriteButtonWidth = 60;
    private const double FavoriteGap = 6;
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(105);
    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(135);

    private readonly List<double> _favoriteSizes = new();
    private IntPtr _targetHwnd;
    private bool _expanded;
    private bool _favoriteMenuOpen;
    private int? _editingFavoriteIndex;
    private long _visibilityAnimationVersion;

    public CaptionResizePillWindow()
    {
        InitializeComponent();
        LoadFavorites();
        RefreshFavoriteButtons(animate: false);
        UpdateFavoriteActionState();
    }

    public event EventHandler<CaptionResizeRequestEventArgs>? ResizeRequested;
    public event EventHandler? LayoutModeChanged;

    public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;
    public bool IsInteractionLocked => IsKeyboardFocusWithin || _favoriteMenuOpen;
    public bool IsExpanded => _expanded;

    public void SetTarget(IntPtr hwnd, int width, int height, double? physicalDiagonalInches, string? displayName)
    {
        var targetChanged = hwnd != _targetHwnd;
        _targetHwnd = hwnd;
        UpdateTargetDimensions(width, height, physicalDiagonalInches, displayName, updateEditor: targetChanged || !IsKeyboardFocusWithin);

        if (targetChanged && _expanded)
        {
            SetExpanded(false, focusEditor: false, animate: false);
        }
    }

    public void UpdateTargetDimensions(
        int width,
        int height,
        double? physicalDiagonalInches,
        string? displayName,
        bool updateEditor = true)
    {
        CurrentSizeText.Text = physicalDiagonalInches is > 0
            ? $"{physicalDiagonalInches.Value:0.#} in"
            : "— in";

        CurrentSizeText.ToolTip = physicalDiagonalInches is > 0
            ? $"{width:N0} × {height:N0} px · {physicalDiagonalInches.Value:0.##} in diagonal" +
              (string.IsNullOrWhiteSpace(displayName) ? string.Empty : $" · {displayName}")
            : "MUX needs a configured physical display size to calculate inches.";

        if (updateEditor && physicalDiagonalInches is > 0 && !_editingFavoriteIndex.HasValue)
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
        SetExpanded(false, focusEditor: false, animate);
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        ExitFavoriteEditMode();
        SetExpanded(true, focusEditor: true, animate: true);
    }

    private void CurrentSize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExitFavoriteEditMode();
        SetExpanded(true, focusEditor: true, animate: true);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(false, focusEditor: false, animate: true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplyRequestedSize();
    }

    private void FavoriteAction_Click(object sender, RoutedEventArgs e)
    {
        SaveOrAddFavorite();
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
            SetExpanded(false, focusEditor: false, animate: true);
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
        SetExpanded(false, focusEditor: false, animate: true);
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
            SetExpanded(false, focusEditor: false, animate: true);
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
        SetExpanded(false, focusEditor: false, animate: true);
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
        args = new CaptionResizeRequestEventArgs(_targetHwnd, diagonal);
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
            SetExpanded(true, focusEditor: true, animate: true);
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
        SetExpanded(true, focusEditor: true, animate: true);
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
                Height = 32,
                Margin = new Thickness(0, 0, FavoriteGap, 0),
                Content = FormatFavorite(value),
                Tag = index,
                ToolTip = $"Resize to {FormatFavorite(value)} · right-click to edit or delete"
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

    private static string GetFavoritesPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "freeform-size-favorites.json");
    }

    private static bool IsValidDiagonal(double diagonal)
    {
        return double.IsFinite(diagonal) && diagonal is >= 1 and <= 500;
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

    private double CalculateCollapsedWidth()
    {
        return BaseCollapsedWidth + _favoriteSizes.Count * (FavoriteButtonWidth + FavoriteGap);
    }

    private void SetExpanded(bool expanded, bool focusEditor, bool animate)
    {
        if (_expanded == expanded && (!expanded || !focusEditor))
        {
            return;
        }

        if (!expanded)
        {
            ExitFavoriteEditMode();
        }

        _expanded = expanded;
        CollapsedPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        var targetWidth = expanded ? ExpandedWidth : CalculateCollapsedWidth();
        SetPillWidth(targetWidth, animate);

        if (expanded && focusEditor)
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

    private void SetPillWidth(double targetWidth, bool animate)
    {
        if (!animate || !IsVisible)
        {
            BeginAnimation(WidthProperty, null);
            Width = targetWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var start = ActualWidth > 0 ? ActualWidth : Width;
        if (Math.Abs(start - targetWidth) < 0.5)
        {
            BeginAnimation(WidthProperty, null);
            Width = targetWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Width = targetWidth;
        var animation = new DoubleAnimation(start, targetWidth, ResizeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            Width = targetWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        LayoutModeChanged?.Invoke(this, EventArgs.Empty);
    }
}
