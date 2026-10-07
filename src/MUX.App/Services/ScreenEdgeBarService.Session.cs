using MUX.Core.Models;

namespace MUX.App.Services;

public sealed partial class ScreenEdgeBarService
{
    private sealed class DisplaySession : IDisposable
    {
        private readonly ScreenBarProfile _profile;
        private readonly Action<ScreenBarProfile> _changed;
        private readonly EdgeBarOverlay _top;
        private readonly EdgeBarOverlay _right;
        private readonly EdgeBarOverlay _bottom;
        private readonly EdgeBarOverlay _left;
        private DisplayProfile _display;
        private PixelRect _frame;
        private uint _dpi = 96;
        private int _minimumThickness = 4;
        private int _topThickness;
        private int _rightThickness;
        private int _bottomThickness;
        private int _leftThickness;
        private bool _disposed;

        public DisplaySession(DisplayProfile display, ScreenBarProfile profile, Action<ScreenBarProfile> changed)
        {
            _display = display;
            _profile = profile;
            _changed = changed;
            UpdateGeometryFromDisplay();
            LoadThicknessesFromProfile();

            _top = CreateOverlay(EdgeBarSide.Top);
            _right = CreateOverlay(EdgeBarSide.Right);
            _bottom = CreateOverlay(EdgeBarSide.Bottom);
            _left = CreateOverlay(EdgeBarSide.Left);
        }

        private EdgeBarOverlay CreateOverlay(EdgeBarSide side)
            => new(side, () => GetThickness(side), value => SetThickness(side, value));

        public void UpdateDisplay(DisplayProfile display)
        {
            if (_disposed)
            {
                return;
            }

            var changed = _display.LeftPx != display.LeftPx ||
                          _display.TopPx != display.TopPx ||
                          _display.WidthPx != display.WidthPx ||
                          _display.HeightPx != display.HeightPx;
            _display = display;
            if (changed)
            {
                UpdateGeometryFromDisplay();
                LoadThicknessesFromProfile();
            }
        }

        public void Refresh(PixelPoint? cursor, bool visible)
        {
            if (_disposed)
            {
                return;
            }

            _top.Update(_frame, _topThickness, _dpi, cursor, visible);
            _right.Update(_frame, _rightThickness, _dpi, cursor, visible);
            _bottom.Update(_frame, _bottomThickness, _dpi, cursor, visible);
            _left.Update(_frame, _leftThickness, _dpi, cursor, visible);
        }

        public void UpdateInput(PixelPoint cursor, bool visible)
        {
            _top.UpdateInput(cursor, visible);
            _right.UpdateInput(cursor, visible);
            _bottom.UpdateInput(cursor, visible);
            _left.UpdateInput(cursor, visible);
        }

        private void UpdateGeometryFromDisplay()
        {
            _frame = new PixelRect(
                _display.LeftPx,
                _display.TopPx,
                _display.LeftPx + Math.Max(1, _display.WidthPx),
                _display.TopPx + Math.Max(1, _display.HeightPx));
            _dpi = EdgeBarOverlay.ScaleDpiForRect(_frame);
            _minimumThickness = EdgeBarOverlay.ScaleForDpi(4, _dpi);
        }

        private void LoadThicknessesFromProfile()
        {
            _topThickness = Scale(_profile.TopRatio, _frame.Height, _minimumThickness);
            _rightThickness = Scale(_profile.RightRatio, _frame.Width, _minimumThickness);
            _bottomThickness = Scale(_profile.BottomRatio, _frame.Height, _minimumThickness);
            _leftThickness = Scale(_profile.LeftRatio, _frame.Width, _minimumThickness);
        }

        private int GetThickness(EdgeBarSide side)
            => side switch
            {
                EdgeBarSide.Top => _topThickness,
                EdgeBarSide.Right => _rightThickness,
                EdgeBarSide.Bottom => _bottomThickness,
                EdgeBarSide.Left => _leftThickness,
                _ => _minimumThickness
            };

        private void SetThickness(EdgeBarSide side, int value)
        {
            if (_disposed)
            {
                return;
            }

            var dimension = side is EdgeBarSide.Top or EdgeBarSide.Bottom ? _frame.Height : _frame.Width;
            var next = Math.Clamp(value, _minimumThickness, Math.Max(_minimumThickness, dimension));

            switch (side)
            {
                case EdgeBarSide.Top:
                    _topThickness = next;
                    _profile.TopRatio = Ratio(next, _frame.Height);
                    break;
                case EdgeBarSide.Right:
                    _rightThickness = next;
                    _profile.RightRatio = Ratio(next, _frame.Width);
                    break;
                case EdgeBarSide.Bottom:
                    _bottomThickness = next;
                    _profile.BottomRatio = Ratio(next, _frame.Height);
                    break;
                case EdgeBarSide.Left:
                    _leftThickness = next;
                    _profile.LeftRatio = Ratio(next, _frame.Width);
                    break;
            }

            _changed(_profile);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SafeDispose(_top);
            SafeDispose(_right);
            SafeDispose(_bottom);
            SafeDispose(_left);
        }
    }

    private sealed class ScreenBarState
    {
        public int Version { get; set; } = StateVersion;
        public bool GloballyVisible { get; set; } = true;
        public List<ScreenBarProfile> Displays { get; set; } = new();
    }

    private sealed class ScreenBarProfile
    {
        public string DeviceName { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public double TopRatio { get; set; }
        public double RightRatio { get; set; }
        public double BottomRatio { get; set; }
        public double LeftRatio { get; set; }

        public bool IsValid()
            => !string.IsNullOrWhiteSpace(DeviceName) &&
               Valid(TopRatio) && Valid(RightRatio) && Valid(BottomRatio) && Valid(LeftRatio);

        private static bool Valid(double value)
            => double.IsFinite(value) && value >= 0 && value <= 1;
    }

    private static double Ratio(int value, int dimension)
        => Math.Clamp((double)Math.Max(0, value) / Math.Max(1, dimension), 0.0, 1.0);

    private static int Scale(double ratio, int dimension, int minimum)
        => Math.Clamp(
            (int)Math.Round(Math.Max(0.0, ratio) * Math.Max(1, dimension)),
            Math.Max(1, minimum),
            Math.Max(Math.Max(1, minimum), dimension));
}
