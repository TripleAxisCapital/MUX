using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MUX.App.Services;

internal sealed partial class EdgeBarOverlay
{
    private void EnsureShownAndPositioned()
    {
        if (!_shown)
        {
            Opacity = 0;
            Show();
            _shown = true;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_positioned || !_lastPositioned.Equals(_overlayRect))
        {
            _ = SetWindowPos(
                hwnd,
                IntPtr.Zero,
                _overlayRect.Left,
                _overlayRect.Top,
                Math.Max(1, _overlayRect.Width),
                Math.Max(1, _overlayRect.Height),
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            _lastPositioned = _overlayRect;
            _positioned = true;
        }

        Opacity = 1;
    }

    private void DrawCover()
    {
        var scale = _dpi / 96.0;
        _canvas.Width = Math.Max(1.0, _overlayRect.Width / scale);
        _canvas.Height = Math.Max(1.0, _overlayRect.Height / scale);

        var frameLeft = (_frame.Left - _overlayRect.Left) / scale;
        var frameTop = (_frame.Top - _overlayRect.Top) / scale;
        var thicknessDip = Math.Max(1.0, _thickness / scale);
        var frameWidthDip = Math.Max(1.0, _frame.Width / scale);
        var frameHeightDip = Math.Max(1.0, _frame.Height / scale);

        if (_side is EdgeBarSide.Top or EdgeBarSide.Bottom)
        {
            _cover.Width = frameWidthDip;
            _cover.Height = Math.Min(frameHeightDip, thicknessDip);
            Canvas.SetLeft(_cover, frameLeft);
            Canvas.SetTop(_cover, _side == EdgeBarSide.Top
                ? frameTop
                : (_frame.Bottom - _thickness - _overlayRect.Top) / scale);
        }
        else
        {
            _cover.Width = Math.Min(frameWidthDip, thicknessDip);
            _cover.Height = frameHeightDip;
            Canvas.SetTop(_cover, frameTop);
            Canvas.SetLeft(_cover, _side == EdgeBarSide.Left
                ? frameLeft
                : (_frame.Right - _thickness - _overlayRect.Left) / scale);
        }
    }

    private void UpdateTopBarReveal(PixelPoint point)
    {
        if (_side != EdgeBarSide.Top)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_dragging)
        {
            _lastTopHideUtc = DateTime.MinValue;
            SetTopBarRevealed(true);
            return;
        }

        if (IsTopFarLeftSafeZone(point))
        {
            _lastTopHideUtc = DateTime.MinValue;
            SetTopBarRevealed(true);
            return;
        }

        var approachAbove = ScaleForDpi(34, _dpi);
        var titleBand = Math.Max(ScaleForDpi(92, _dpi), _thickness + ScaleForDpi(54, _dpi));
        var approachingCaption =
            point.X >= _frame.Left && point.X < _frame.Right &&
            point.Y >= _frame.Top - approachAbove &&
            point.Y <= Math.Min(_frame.Bottom, _frame.Top + titleBand);

        if (approachingCaption)
        {
            _lastTopHideUtc = now;
            SetTopBarRevealed(false);
            return;
        }

        if (_lastTopHideUtc == DateTime.MinValue || now - _lastTopHideUtc >= TimeSpan.FromMilliseconds(180))
        {
            SetTopBarRevealed(true);
        }
    }

    private bool IsTopFarLeftSafeZone(PixelPoint point)
    {
        if (_side != EdgeBarSide.Top)
        {
            return false;
        }

        var safeWidth = ScaleForDpi(112, _dpi);
        var verticalPad = ScaleForDpi(34, _dpi);
        return point.X >= _frame.Left && point.X <= Math.Min(_frame.Right, _frame.Left + safeWidth) &&
               point.Y >= _frame.Top - verticalPad &&
               point.Y <= Math.Min(_frame.Bottom, _frame.Top + _thickness + ScaleForDpi(36, _dpi));
    }

    private void SetTopBarRevealed(bool revealed)
    {
        if (_side != EdgeBarSide.Top || _topBarRevealed == revealed)
        {
            return;
        }

        _topBarRevealed = revealed;
        UpdateGripOpacity();
        _cover.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(revealed ? 1.0 : 0.0, TimeSpan.FromMilliseconds(revealed ? 135 : 85))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void DrawHandle(PixelPoint? cursor)
    {
        var point = cursor;
        var hot = _dragging || (point is PixelPoint current && IsInteractivePoint(current));
        SetHot(hot);

        var scale = _dpi / 96.0;
        var boundary = InnerBoundary();
        var longPx = ScaleForDpi(_side == EdgeBarSide.Top ? 40 : 64, _dpi);
        var shortPx = ScaleForDpi(_side == EdgeBarSide.Top ? 5 : 24, _dpi);
        var center = HandleCenter();

        if (_side is EdgeBarSide.Top or EdgeBarSide.Bottom)
        {
            _handle.Width = longPx / scale;
            _handle.Height = shortPx / scale;
            Canvas.SetLeft(_handle, (center - longPx / 2 - _overlayRect.Left) / scale);
            Canvas.SetTop(_handle, (boundary - shortPx / 2 - _overlayRect.Top) / scale);
        }
        else
        {
            _handle.Width = shortPx / scale;
            _handle.Height = longPx / scale;
            Canvas.SetLeft(_handle, (boundary - shortPx / 2 - _overlayRect.Left) / scale);
            Canvas.SetTop(_handle, (center - longPx / 2 - _overlayRect.Top) / scale);
        }
    }

    private void SetHot(bool hot)
    {
        if (_hot != hot)
        {
            _hot = hot;
            var easing = new CubicEase { EasingMode = hot ? EasingMode.EaseOut : EasingMode.EaseIn };
            var resting = _side == EdgeBarSide.Top ? 0.94 : 0.86;
            _handleScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(_handleScale.ScaleX, hot ? 1.0 : resting, GripAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
            _handleScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(_handleScale.ScaleY, hot ? 1.0 : resting, GripAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
        }

        UpdateGripOpacity();
    }

    private void UpdateGripOpacity()
    {
        var desired = _hot
            ? 1.0
            : _side == EdgeBarSide.Top && _topBarRevealed
                ? 0.32
                : 0.0;

        if (Math.Abs(_lastGripOpacity - desired) < 0.001)
        {
            return;
        }

        _lastGripOpacity = desired;
        _handle.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(desired, GripAnimation)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private bool IsInteractivePoint(PixelPoint point)
    {
        if (_disposed || !_shown)
        {
            return false;
        }

        if (_side == EdgeBarSide.Top)
        {
            return _dragging || (_topBarRevealed && IsTopGripHit(point) &&
                (!_leftButtonWasDown || _pressStartedOnGrip));
        }

        var boundary = InnerBoundary();
        var nearBoundary = _side is EdgeBarSide.Top or EdgeBarSide.Bottom
            ? Math.Abs(point.Y - boundary) <= _grabRadius
            : Math.Abs(point.X - boundary) <= _grabRadius;
        var nearHandle = _side is EdgeBarSide.Top or EdgeBarSide.Bottom
            ? Math.Abs(point.X - HandleCenter()) <= ScaleForDpi(36, _dpi)
            : Math.Abs(point.Y - HandleCenter()) <= ScaleForDpi(36, _dpi);
        return _dragging || (nearBoundary && nearHandle);
    }

    private bool IsTopGripHit(PixelPoint point)
        => _topBarRevealed &&
           Math.Abs(point.X - HandleCenter()) <= ScaleForDpi(34, _dpi) &&
           Math.Abs(point.Y - InnerBoundary()) <= ScaleForDpi(13, _dpi);

    private int HandleCenter()
    {
        var horizontal = _side is EdgeBarSide.Top or EdgeBarSide.Bottom;
        var start = horizontal ? _frame.Left : _frame.Top;
        var end = horizontal ? _frame.Right : _frame.Bottom;
        var desired = _side == EdgeBarSide.Top
            ? start + ScaleForDpi(56, _dpi)
            : start + Math.Max(0, end - start) / 2;
        return ClampCenter(desired, start, end, ScaleForDpi(68, _dpi), ScaleForDpi(8, _dpi));
    }

    private int InnerBoundary()
        => _side switch
        {
            EdgeBarSide.Top => _frame.Top + _thickness,
            EdgeBarSide.Bottom => _frame.Bottom - _thickness,
            EdgeBarSide.Left => _frame.Left + _thickness,
            EdgeBarSide.Right => _frame.Right - _thickness,
            _ => 0
        };

}
