using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DualClip.Encoding;

namespace DualClip.App;

public partial class MainWindow
{
    private readonly List<TimelineSegment> _timelineSegments = [];
    private TimelineSegment? _selectedTimelineSegment;
    private TimelineSegment? _copiedTimelineSegment;
    private bool _isTimelinePlaybackActive;

    private void InitializeTimelineForLoadedClip()
    {
        ClearTimelineUndoHistory();
        _timelineSegments.Clear();
        _copiedTimelineSegment = null;
        _selectedTimelineSegment = null;

        if (_selectedClipDurationSeconds <= 0 || _selectedClipWidth <= 0 || _selectedClipHeight <= 0)
        {
            UpdateEditorVisuals();
            return;
        }

        var initialSegment = new TimelineSegment
        {
            SourceClipPath = GetSelectedClip()?.FilePath ?? string.Empty,
            SourceStartSeconds = 0,
            SourceEndSeconds = _selectedClipDurationSeconds,
            CropRectSource = new Rect(0, 0, _selectedClipWidth, _selectedClipHeight),
            ScalePercent = 100d,
            OpacityPercent = 100d,
            PlaybackSpeed = 1d,
            ZoomKeyframe1OffsetSeconds = 0,
            ZoomKeyframe2OffsetSeconds = _selectedClipDurationSeconds,
            ZoomKeyframe1Percent = 100,
            ZoomKeyframe2Percent = 100,
        };

        _timelineSegments.Add(initialSegment);
        NormalizeTimelineSegmentPositions();
        SelectTimelineSegment(initialSegment, movePlayheadToSegmentStart: true);
    }

    private void ClearTimeline()
    {
        _timelineSegments.Clear();
        _selectedTimelineSegment = null;
        _copiedTimelineSegment = null;
        _isTimelinePlaybackActive = false;
        TimelineSegmentsCanvas.Children.Clear();
    }

    private void SelectTimelineSegment(TimelineSegment? segment, bool movePlayheadToSegmentStart = false)
    {
        ApplyCurrentEditorStateToSelectedSegment();

        _selectedTimelineSegment = segment is not null && _timelineSegments.Contains(segment) ? segment : null;

        if (_selectedTimelineSegment is null)
        {
            _trimStartSeconds = 0;
            _trimEndSeconds = 0;
            _zoomKeyframe1Seconds = 0;
            _zoomKeyframe2Seconds = 0;
            _zoomKeyframe1Percent = 100;
            _zoomKeyframe2Percent = 100;
            _cropRectSource = Rect.Empty;
            _rotationDegrees = 0d;
            _scalePercent = 100d;
            _translateX = 0d;
            _translateY = 0d;
            _opacityPercent = 100d;
            _flipHorizontal = false;
            _flipVertical = false;
            UpdateZoomSlidersFromState();
            UpdateTransformControlsFromState();
            UpdateEditorVisuals();
            return;
        }

        LoadSelectedSegmentIntoEditorState(_selectedTimelineSegment);

        if (movePlayheadToSegmentStart)
        {
            _playheadSeconds = GetSelectedSegmentTimelineStartSeconds();
        }

        UpdateEditorVisuals();
    }

    private void LoadSelectedSegmentIntoEditorState(TimelineSegment segment)
    {
        _trimStartSeconds = segment.SourceStartSeconds;
        _trimEndSeconds = segment.SourceEndSeconds;
        _zoomKeyframe1Seconds = Math.Clamp(segment.ZoomKeyframe1OffsetSeconds, 0, segment.DurationSeconds);
        _zoomKeyframe2Seconds = Math.Clamp(segment.ZoomKeyframe2OffsetSeconds, 0, segment.DurationSeconds);
        _zoomKeyframe1Percent = segment.ZoomKeyframe1Percent;
        _zoomKeyframe2Percent = segment.ZoomKeyframe2Percent;
        _cropRectSource = segment.CropRectSource;
        _rotationDegrees = segment.RotationDegrees;
        _scalePercent = segment.ScalePercent;
        _translateX = segment.TranslateX;
        _translateY = segment.TranslateY;
        _opacityPercent = segment.OpacityPercent;
        _flipHorizontal = segment.FlipHorizontal;
        _flipVertical = segment.FlipVertical;

        if (_cropRectSource.IsEmpty || _cropRectSource.Width <= 0 || _cropRectSource.Height <= 0)
        {
            _cropRectSource = new Rect(0, 0, _selectedClipWidth, _selectedClipHeight);
        }

        UpdateZoomSlidersFromState();
        UpdateTransformControlsFromState();
    }

    private void ApplyCurrentEditorStateToSelectedSegment()
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        _selectedTimelineSegment.SourceStartSeconds = _trimStartSeconds;
        _selectedTimelineSegment.SourceEndSeconds = _trimEndSeconds;
        _selectedTimelineSegment.ZoomKeyframe1OffsetSeconds = Math.Clamp(_zoomKeyframe1Seconds, 0, _selectedTimelineSegment.DurationSeconds);
        _selectedTimelineSegment.ZoomKeyframe2OffsetSeconds = Math.Clamp(_zoomKeyframe2Seconds, 0, _selectedTimelineSegment.DurationSeconds);
        _selectedTimelineSegment.ZoomKeyframe1Percent = _zoomKeyframe1Percent;
        _selectedTimelineSegment.ZoomKeyframe2Percent = _zoomKeyframe2Percent;
        _selectedTimelineSegment.CropRectSource = _cropRectSource;
        _selectedTimelineSegment.RotationDegrees = _rotationDegrees;
        _selectedTimelineSegment.ScalePercent = _scalePercent;
        _selectedTimelineSegment.TranslateX = _translateX;
        _selectedTimelineSegment.TranslateY = _translateY;
        _selectedTimelineSegment.OpacityPercent = _opacityPercent;
        _selectedTimelineSegment.FlipHorizontal = _flipHorizontal;
        _selectedTimelineSegment.FlipVertical = _flipVertical;
    }

    private double GetTimelineDurationSeconds()
    {
        return _timelineSegments.Count == 0
            ? 0
            : _timelineSegments.Max(item => item.TimelineStartSeconds + item.DurationSeconds);
    }

    private double GetSelectedSegmentTimelineStartSeconds()
    {
        return _selectedTimelineSegment is null ? 0 : GetSegmentTimelineStartSeconds(_selectedTimelineSegment);
    }

    private double GetSelectedSegmentDurationSeconds()
    {
        return _selectedTimelineSegment?.DurationSeconds ?? 0;
    }

    private double GetSegmentTimelineStartSeconds(TimelineSegment segment)
    {
        return segment.TimelineStartSeconds;
    }

    private void NormalizeTimelineSegmentPositions()
    {
        double currentStart = 0;

        foreach (var segment in _timelineSegments)
        {
            segment.TimelineStartSeconds = currentStart;
            currentStart += segment.DurationSeconds;
        }
    }

    private bool TryGetSelectedSegmentIndex(out int index)
    {
        index = _selectedTimelineSegment is null ? -1 : _timelineSegments.IndexOf(_selectedTimelineSegment);
        return index >= 0;
    }

    private bool TryFindSegmentAtTimelineTime(double timeSeconds, out TimelineSegment? segment, out double segmentTimelineStart, out double localOffsetSeconds)
    {
        segment = null;
        segmentTimelineStart = 0;
        localOffsetSeconds = 0;

        if (_timelineSegments.Count == 0)
        {
            return false;
        }

        double currentStart = 0;

        foreach (var item in _timelineSegments)
        {
            var currentEnd = currentStart + item.DurationSeconds;
            var isFirstSegment = ReferenceEquals(item, _timelineSegments[0]);
            var isLastSegment = ReferenceEquals(item, _timelineSegments[^1]);

            if ((isFirstSegment && timeSeconds <= currentEnd && timeSeconds <= 0)
                || timeSeconds < currentEnd
                || isLastSegment)
            {
                segment = item;
                segmentTimelineStart = currentStart;
                localOffsetSeconds = Math.Clamp(timeSeconds - currentStart, 0, item.DurationSeconds);
                return true;
            }

            currentStart = currentEnd;
        }

        return false;
    }

    private void RenderTimelineSegments()
    {
        TimelineSegmentsCanvas.Children.Clear();

        var timelineWidth = GetTimelineCanvasWidth();

        if (timelineWidth <= 0 || TimelineCanvas.Height <= 0)
        {
            return;
        }

        TimelineSegmentsCanvas.Width = timelineWidth;
        TimelineSegmentsCanvas.Height = TimelineCanvas.Height;

        if (_timelineSegments.Count == 0)
        {
            return;
        }

        var mutedBrush = TryFindResource("MutedTextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
        var panelBrush = TryFindResource("PanelBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DimGray;
        var accentSurfaceBrush = TryFindResource("AccentSurfaceBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightSteelBlue;
        var accentBrush = TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Goldenrod;
        var borderBrush = TryFindResource("BorderBrushDark") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
        var textBrush = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;

        for (var index = 0; index < _timelineSegments.Count; index++)
        {
            var segment = _timelineSegments[index];
            var start = GetSegmentTimelineStartSeconds(segment);
            var end = start + segment.DurationSeconds;
            var left = TimeToTimelineX(start);
            var right = TimeToTimelineX(end);
            var width = Math.Max(64, right - left);
            var segmentTitle = string.IsNullOrWhiteSpace(segment.SourceClipPath)
                ? $"Clip {index + 1}"
                : System.IO.Path.GetFileNameWithoutExtension(segment.SourceClipPath);

            var title = new TextBlock
            {
                Text = segmentTitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var subtitle = new TextBlock
            {
                Text = $"{segment.DurationSeconds:0.00}s",
                FontSize = 11,
                Foreground = mutedBrush,
            };

            var stack = new StackPanel
            {
                Margin = new Thickness(8, 4, 8, 4),
            };
            stack.Children.Add(title);
            stack.Children.Add(subtitle);

            var border = new Border
            {
                Width = width,
                Height = 48,
                CornerRadius = new CornerRadius(8),
                BorderBrush = ReferenceEquals(segment, _selectedTimelineSegment) ? accentBrush : borderBrush,
                BorderThickness = ReferenceEquals(segment, _selectedTimelineSegment) ? new Thickness(1.5) : new Thickness(1),
                Background = ReferenceEquals(segment, _selectedTimelineSegment) ? accentSurfaceBrush : panelBrush,
                Child = stack,
                Cursor = System.Windows.Input.Cursors.SizeAll,
                Tag = segment,
            };

            border.MouseLeftButtonDown += TimelineSegmentBorder_MouseLeftButtonDown;
            border.MouseMove += TimelineSegmentBorder_MouseMove;
            border.MouseLeftButtonUp += TimelineSegmentBorder_MouseLeftButtonUp;
            border.LostMouseCapture += TimelineSegmentBorder_LostMouseCapture;
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, 18);
            System.Windows.Controls.Panel.SetZIndex(border, 1);
            TimelineSegmentsCanvas.Children.Add(border);
        }
    }

    private void TimelineSegmentBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not TimelineSegment segment)
        {
            return;
        }

        SelectTimelineSegment(segment);
        _playheadSeconds = GetSegmentTimelineStartSeconds(segment);
        _draggingTimelineSegment = segment;
        _timelineSegmentDragStartPoint = e.GetPosition(TimelineSegmentsCanvas);
        _timelineSegmentDragOriginStartSeconds = segment.TimelineStartSeconds;
        _timelineSegmentDragCurrentStartSeconds = segment.TimelineStartSeconds;
        _timelineSegmentDropIndex = _timelineSegments.IndexOf(segment);
        _isTimelineSegmentDragging = true;
        _didTimelineSegmentDragMove = false;
        border.CaptureMouse();
        SeekToPlayhead(updatePreviewPosition: true);
        e.Handled = true;
    }

    private void TimelineSegmentBorder_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isTimelineSegmentDragging
            || _draggingTimelineSegment is null
            || sender is not Border
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(TimelineSegmentsCanvas);
        var deltaX = currentPoint.X - _timelineSegmentDragStartPoint.X;

        if (!_didTimelineSegmentDragMove && Math.Abs(deltaX) < TimelineDragActivationPixels)
        {
            return;
        }

        if (!_didTimelineSegmentDragMove)
        {
            CaptureTimelineUndoSnapshot();
            _didTimelineSegmentDragMove = true;
        }

        _timelineSegmentDragCurrentStartSeconds = Math.Clamp(_timelineSegmentDragOriginStartSeconds + TimelineDeltaToTime(deltaX), 0, GetTimelineDurationSeconds());
        _timelineSegmentDropIndex = GetTimelineSegmentDropIndex(_timelineSegmentDragCurrentStartSeconds, _draggingTimelineSegment);
        UpdateTimelineDropIndicator(GetTimelineDropTime(_timelineSegmentDropIndex, _draggingTimelineSegment));
    }

    private void TimelineSegmentBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        CompleteTimelineSegmentDrag();
    }

    private void TimelineSegmentBorder_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CompleteTimelineSegmentDrag();
    }

    private void CompleteTimelineSegmentDrag()
    {
        if (!_isTimelineSegmentDragging || _draggingTimelineSegment is null)
        {
            UpdateTimelineDropIndicator();
            return;
        }

        try
        {
            if (_didTimelineSegmentDragMove)
            {
                var segment = _draggingTimelineSegment;
                var originalIndex = _timelineSegments.IndexOf(segment);

                if (originalIndex >= 0)
                {
                    _timelineSegments.RemoveAt(originalIndex);
                    var insertIndex = Math.Clamp(_timelineSegmentDropIndex, 0, _timelineSegments.Count);
                    _timelineSegments.Insert(insertIndex, segment);
                    NormalizeTimelineSegmentPositions();
                    SelectTimelineSegment(segment, movePlayheadToSegmentStart: true);
                    _viewModel.EditorStatus = "Moved the selected segment.";
                }
            }
        }
        finally
        {
            _draggingTimelineSegment = null;
            _isTimelineSegmentDragging = false;
            _didTimelineSegmentDragMove = false;
            _timelineSegmentDropIndex = -1;
            UpdateTimelineDropIndicator();
            UpdateEditorVisuals();
        }
    }

    private int GetTimelineSegmentDropIndex(double targetStartSeconds, TimelineSegment draggingSegment)
    {
        var probeTime = SnapTimelineTimeValue(targetStartSeconds, 0, _playheadSeconds, GetTimelineDurationSeconds());
        var index = 0;

        foreach (var segment in _timelineSegments)
        {
            if (ReferenceEquals(segment, draggingSegment))
            {
                continue;
            }

            var segmentMidpoint = segment.TimelineStartSeconds + (segment.DurationSeconds / 2d);

            if (probeTime < segmentMidpoint)
            {
                return index;
            }

            index++;
        }

        return index;
    }

    private double GetTimelineDropTime(int dropIndex, TimelineSegment draggingSegment)
    {
        double currentTime = 0;
        var index = 0;

        foreach (var segment in _timelineSegments)
        {
            if (ReferenceEquals(segment, draggingSegment))
            {
                continue;
            }

            if (index == dropIndex)
            {
                return currentTime;
            }

            currentTime += segment.DurationSeconds;
            index++;
        }

        return currentTime;
    }

    private double GetPlayheadOffsetWithinSelectedSegment()
    {
        var segmentDuration = GetSelectedSegmentDurationSeconds();
        return Math.Clamp(_playheadSeconds - GetSelectedSegmentTimelineStartSeconds(), 0, segmentDuration);
    }

    private TimelineEditRequest BuildTimelineEditRequest(string inputPath, string outputPath)
    {
        ApplyCurrentEditorStateToSelectedSegment();
        NormalizeTimelineSegmentPositions();

        return new TimelineEditRequest
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            FfmpegPath = _viewModel.FfmpegPath,
            SourceWidth = _selectedClipWidth,
            SourceHeight = _selectedClipHeight,
            FpsTarget = int.TryParse(_viewModel.FpsTargetText, out var fpsTarget) && fpsTarget > 0 ? fpsTarget : 30,
            Segments = _timelineSegments.Select(segment => new TimelineEditSegment
            {
                SourceClipPath = segment.SourceClipPath,
                SourceStartSeconds = segment.SourceStartSeconds,
                SourceEndSeconds = segment.SourceEndSeconds,
                TimelineStartSeconds = segment.TimelineStartSeconds,
                CropX = IsSegmentCropActive(segment) ? (int)Math.Round(segment.CropRectSource.X) : null,
                CropY = IsSegmentCropActive(segment) ? (int)Math.Round(segment.CropRectSource.Y) : null,
                CropWidth = IsSegmentCropActive(segment) ? (int)Math.Round(segment.CropRectSource.Width) : null,
                CropHeight = IsSegmentCropActive(segment) ? (int)Math.Round(segment.CropRectSource.Height) : null,
                RotationDegrees = segment.RotationDegrees,
                ScalePercent = segment.ScalePercent,
                TranslateX = segment.TranslateX,
                TranslateY = segment.TranslateY,
                FlipHorizontal = segment.FlipHorizontal,
                FlipVertical = segment.FlipVertical,
                OpacityPercent = segment.OpacityPercent,
                ZoomKeyframe1TimeSeconds = null,
                ZoomKeyframe2TimeSeconds = null,
                ZoomKeyframe1Percent = null,
                ZoomKeyframe2Percent = null,
            }).ToList(),
        };
    }

    private bool IsSegmentCropActive(TimelineSegment segment)
    {
        return Math.Abs(segment.CropRectSource.X) > 0.5
            || Math.Abs(segment.CropRectSource.Y) > 0.5
            || Math.Abs(segment.CropRectSource.Width - _selectedClipWidth) > 0.5
            || Math.Abs(segment.CropRectSource.Height - _selectedClipHeight) > 0.5;
    }

    private static bool IsSegmentZoomActive(TimelineSegment segment)
    {
        return Math.Abs(segment.ZoomKeyframe1Percent - 100d) > 0.01d || Math.Abs(segment.ZoomKeyframe2Percent - 100d) > 0.01d;
    }

    private void SplitSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null || !TryGetSelectedSegmentIndex(out var index))
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        ApplyCurrentEditorStateToSelectedSegment();

        var segmentDuration = _selectedTimelineSegment.DurationSeconds;
        var localOffset = GetPlayheadOffsetWithinSelectedSegment();

        if (localOffset <= MinimumTrimDurationSeconds || localOffset >= segmentDuration - MinimumTrimDurationSeconds)
        {
            _viewModel.EditorStatus = "Move the playhead inside the selected segment before splitting.";
            return;
        }

        var splitTimelineTime = _playheadSeconds;
        var splitSourceTime = _selectedTimelineSegment.SourceStartSeconds + localOffset;
        var secondSegment = _selectedTimelineSegment.DuplicateForTimeline();
        secondSegment.SourceStartSeconds = splitSourceTime;
        secondSegment.ZoomKeyframe1OffsetSeconds = Math.Clamp(secondSegment.ZoomKeyframe1OffsetSeconds - localOffset, 0, secondSegment.DurationSeconds);
        secondSegment.ZoomKeyframe2OffsetSeconds = Math.Clamp(secondSegment.ZoomKeyframe2OffsetSeconds - localOffset, 0, secondSegment.DurationSeconds);

        _selectedTimelineSegment.SourceEndSeconds = splitSourceTime;
        _selectedTimelineSegment.ZoomKeyframe1OffsetSeconds = Math.Clamp(_selectedTimelineSegment.ZoomKeyframe1OffsetSeconds, 0, _selectedTimelineSegment.DurationSeconds);
        _selectedTimelineSegment.ZoomKeyframe2OffsetSeconds = Math.Clamp(_selectedTimelineSegment.ZoomKeyframe2OffsetSeconds, 0, _selectedTimelineSegment.DurationSeconds);
        LoadSelectedSegmentIntoEditorState(_selectedTimelineSegment);

        _timelineSegments.Insert(index + 1, secondSegment);
        NormalizeTimelineSegmentPositions();
        SelectTimelineSegment(secondSegment);
        _playheadSeconds = Math.Clamp(splitTimelineTime, 0, GetTimelineDurationSeconds());
        SeekToPlayhead(updatePreviewPosition: true);
        _viewModel.EditorStatus = "Split the selected segment at the playhead.";
    }

    private void CopySegmentButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCurrentEditorStateToSelectedSegment();

        if (_selectedTimelineSegment is null)
        {
            return;
        }

        _copiedTimelineSegment = _selectedTimelineSegment.CloneForSnapshot();
        _viewModel.EditorStatus = "Copied the selected segment.";
    }

    private void PasteSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedTimelineSegment is null)
        {
            _viewModel.EditorStatus = "Copy a segment first.";
            return;
        }

        CaptureTimelineUndoSnapshot();
        ApplyCurrentEditorStateToSelectedSegment();

        var insertIndex = TryGetSelectedSegmentIndex(out var selectedIndex) ? selectedIndex + 1 : _timelineSegments.Count;
        var pasted = _copiedTimelineSegment.DuplicateForTimeline();
        _timelineSegments.Insert(insertIndex, pasted);
        NormalizeTimelineSegmentPositions();
        SelectTimelineSegment(pasted, movePlayheadToSegmentStart: true);
        _viewModel.EditorStatus = "Pasted the copied segment into the timeline.";
    }

    private void DeleteSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null || !TryGetSelectedSegmentIndex(out var index))
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        _timelineSegments.RemoveAt(index);
        NormalizeTimelineSegmentPositions();

        if (_timelineSegments.Count == 0)
        {
            PausePreviewPlayback();
            _selectedTimelineSegment = null;
            _playheadSeconds = 0;
            _trimStartSeconds = 0;
            _trimEndSeconds = 0;
            _zoomKeyframe1Seconds = 0;
            _zoomKeyframe2Seconds = 0;
            _zoomKeyframe1Percent = 100;
            _zoomKeyframe2Percent = 100;
            _cropRectSource = Rect.Empty;
            _rotationDegrees = 0d;
            _scalePercent = 100d;
            _translateX = 0d;
            _translateY = 0d;
            _opacityPercent = 100d;
            _flipHorizontal = false;
            _flipVertical = false;
            UpdateZoomSlidersFromState();
            UpdateTransformControlsFromState();
            UpdateEditorVisuals();
            _viewModel.EditorStatus = "Deleted the final segment.";
            return;
        }

        var nextIndex = Math.Clamp(index, 0, _timelineSegments.Count - 1);
        SelectTimelineSegment(_timelineSegments[nextIndex], movePlayheadToSegmentStart: true);
        _viewModel.EditorStatus = "Deleted the selected segment.";
    }
}
