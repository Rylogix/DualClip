using System.Windows;
using System.Windows.Controls.Primitives;

namespace DualClip.App;

public partial class MainWindow
{
    private void SetPlayheadToSegmentStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        _playheadSeconds = GetSelectedSegmentTimelineStartSeconds();
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private void SetPlayheadToSegmentEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        _playheadSeconds = GetSelectedSegmentTimelineStartSeconds() + GetSelectedSegmentDurationSeconds();
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private void CropTopThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromEdge(0, e.VerticalChange, CropResizeEdge.Top);
    }

    private void CropRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromEdge(e.HorizontalChange, 0, CropResizeEdge.Right);
    }

    private void CropBottomThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromEdge(0, e.VerticalChange, CropResizeEdge.Bottom);
    }

    private void CropLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromEdge(e.HorizontalChange, 0, CropResizeEdge.Left);
    }

    private void ResizeCropFromEdge(double horizontalChange, double verticalChange, CropResizeEdge edge)
    {
        if (!TryGetPreviewSourceDelta(horizontalChange, verticalChange, out var deltaX, out var deltaY))
        {
            return;
        }

        var rect = _cropRectSource;
        var aspectRatio = GetLockedCropAspectRatio();

        switch (edge)
        {
            case CropResizeEdge.Top:
            {
                var bottom = rect.Bottom;
                var nextTop = Math.Clamp(rect.Top + deltaY, 0, bottom - MinimumCropSizePixels);
                rect = new Rect(rect.Left, nextTop, rect.Width, bottom - nextTop);
                rect = ApplyAspectRatioAfterVerticalResize(rect, anchorTop: false, aspectRatio);
                break;
            }
            case CropResizeEdge.Right:
            {
                var nextWidth = Math.Clamp(rect.Width + deltaX, MinimumCropSizePixels, _selectedClipWidth - rect.Left);
                rect = new Rect(rect.Left, rect.Top, nextWidth, rect.Height);
                rect = ApplyAspectRatioAfterHorizontalResize(rect, anchorLeft: true, aspectRatio);
                break;
            }
            case CropResizeEdge.Bottom:
            {
                var nextHeight = Math.Clamp(rect.Height + deltaY, MinimumCropSizePixels, _selectedClipHeight - rect.Top);
                rect = new Rect(rect.Left, rect.Top, rect.Width, nextHeight);
                rect = ApplyAspectRatioAfterVerticalResize(rect, anchorTop: true, aspectRatio);
                break;
            }
            case CropResizeEdge.Left:
            {
                var right = rect.Right;
                var nextLeft = Math.Clamp(rect.Left + deltaX, 0, right - MinimumCropSizePixels);
                rect = new Rect(nextLeft, rect.Top, right - nextLeft, rect.Height);
                rect = ApplyAspectRatioAfterHorizontalResize(rect, anchorLeft: false, aspectRatio);
                break;
            }
        }

        rect = ClampCropRect(rect);
        _cropRectSource = rect;
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateCropOverlay();
    }

    private double? GetLockedCropAspectRatio()
    {
        return null;
    }

    private Rect ApplyAspectRatioAfterHorizontalResize(Rect rect, bool anchorLeft, double? aspectRatio)
    {
        if (aspectRatio is null)
        {
            return rect;
        }

        var newHeight = Math.Max(MinimumCropSizePixels, rect.Width / aspectRatio.Value);
        var centerY = rect.Top + (rect.Height / 2d);
        var nextTop = centerY - (newHeight / 2d);
        return new Rect(anchorLeft ? rect.Left : rect.Right - rect.Width, nextTop, rect.Width, newHeight);
    }

    private Rect ApplyAspectRatioAfterVerticalResize(Rect rect, bool anchorTop, double? aspectRatio)
    {
        if (aspectRatio is null)
        {
            return rect;
        }

        var newWidth = Math.Max(MinimumCropSizePixels, rect.Height * aspectRatio.Value);
        var centerX = rect.Left + (rect.Width / 2d);
        var nextLeft = centerX - (newWidth / 2d);
        return new Rect(nextLeft, anchorTop ? rect.Top : rect.Bottom - rect.Height, newWidth, rect.Height);
    }

    private Rect ClampCropRect(Rect rect)
    {
        var width = Math.Clamp(rect.Width, MinimumCropSizePixels, _selectedClipWidth);
        var height = Math.Clamp(rect.Height, MinimumCropSizePixels, _selectedClipHeight);
        var x = Math.Clamp(rect.X, 0, Math.Max(0, _selectedClipWidth - width));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, _selectedClipHeight - height));
        return new Rect(x, y, width, height);
    }

    private enum CropResizeEdge
    {
        Top,
        Right,
        Bottom,
        Left,
    }
}
