using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfPoint = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace DualClip.App;

public partial class MainWindow
{
    private const double TimelineLeftPaddingPixels = 24d;
    private const double TimelineRightPaddingPixels = 40d;
    private const double TimelineSnapThresholdPixels = 16d;
    private const double TimelineDragActivationPixels = 4d;
    private const double PreviewMoveDeadzonePixels = 1d;
    private const double TimelineSegmentTopPixels = 8d;
    private const double TimelineSegmentHeightPixels = 42d;
    private const double TimelineTrackTopPixels = 76d;
    private const double TimelinePlayheadTopPixels = 12d;
    private const double TimelineTrimThumbTopPixels = 64d;

    private TimelineSegment? _copiedSegmentSettings;
    private TimelineSegment? _draggingTimelineSegment;
    private WpfPoint _timelineSegmentDragStartPoint;
    private double _timelineSegmentDragOriginStartSeconds;
    private double _timelineSegmentDragCurrentStartSeconds;
    private int _timelineSegmentDropIndex = -1;
    private bool _isTimelineSegmentDragging;
    private bool _didTimelineSegmentDragMove;

    private double GetTimelinePixelsPerSecond()
    {
        return 100d;
    }

    private double GetTimelineCanvasWidth()
    {
        var viewportWidth = TimelineScrollViewer?.ViewportWidth ?? 0;
        var contentWidth = TimelineLeftPaddingPixels + TimelineRightPaddingPixels + (GetTimelineDurationSeconds() * GetTimelinePixelsPerSecond());
        return Math.Max(Math.Max(420d, viewportWidth), contentWidth);
    }

    private double GetTimelineSnapThresholdSeconds()
    {
        return TimelineSnapThresholdPixels / Math.Max(1d, GetTimelinePixelsPerSecond());
    }

    private double SnapTimelineTimeValue(double proposedSeconds, params double[] candidates)
    {
        if (!_viewModel.Editor.IsSnappingEnabled || candidates.Length == 0)
        {
            return proposedSeconds;
        }

        var threshold = GetTimelineSnapThresholdSeconds();
        var nearest = proposedSeconds;
        var nearestDelta = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var delta = Math.Abs(candidate - proposedSeconds);

            if (delta <= threshold && delta < nearestDelta)
            {
                nearest = candidate;
                nearestDelta = delta;
            }
        }

        return nearest;
    }

    private double SnapLocalSegmentTime(double proposedSeconds, params double[] candidates)
    {
        return Math.Clamp(SnapTimelineTimeValue(proposedSeconds, candidates), 0, GetSelectedSegmentDurationSeconds());
    }

    private double[] GetTimelineGuideTimes()
    {
        var guides = new List<double>
        {
            0,
            _playheadSeconds,
            GetTimelineDurationSeconds(),
        };

        foreach (var segment in _timelineSegments)
        {
            guides.Add(segment.TimelineStartSeconds);
            guides.Add(segment.TimelineStartSeconds + segment.DurationSeconds);
        }

        return guides
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }

    private double[] GetSelectedSegmentGuideTimes()
    {
        if (_selectedTimelineSegment is null)
        {
            return [0];
        }

        var segmentStart = GetSelectedSegmentTimelineStartSeconds();
        var segmentDuration = GetSelectedSegmentDurationSeconds();

        return GetTimelineGuideTimes()
            .Select(value => value - segmentStart)
            .Where(value => value >= 0 && value <= segmentDuration)
            .Concat([0d, segmentDuration])
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }

    private void ScrollPlayheadIntoView()
    {
        if (TimelineScrollViewer is null || TimelineScrollViewer.ViewportWidth <= 0)
        {
            return;
        }

        var playheadX = TimeToTimelineX(_playheadSeconds);
        var margin = 80d;
        var leftEdge = TimelineScrollViewer.HorizontalOffset + margin;
        var rightEdge = TimelineScrollViewer.HorizontalOffset + TimelineScrollViewer.ViewportWidth - margin;

        if (playheadX < leftEdge)
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, playheadX - margin));
        }
        else if (playheadX > rightEdge)
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, playheadX - TimelineScrollViewer.ViewportWidth + margin));
        }
    }

    private void UpdateTimelineDropIndicator(double? insertionTimeSeconds = null)
    {
        if (TimelineDropIndicator is null)
        {
            return;
        }

        if (insertionTimeSeconds is null)
        {
            TimelineDropIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var x = TimeToTimelineX(insertionTimeSeconds.Value);
        Canvas.SetLeft(TimelineDropIndicator, x - (TimelineDropIndicator.Width / 2d));
        Canvas.SetTop(TimelineDropIndicator, 8);
        TimelineDropIndicator.Visibility = Visibility.Visible;
    }

    private void UpdateEditorToolButtons()
    {
        if (ToolCropButton is null || ToolTransformButton is null)
        {
            return;
        }

        ToolCropButton.IsChecked = _viewModel.Editor.IsCropToolActive;
        ToolTransformButton.IsChecked = _viewModel.Editor.IsTransformToolActive;
    }

    private void SetActiveTool(EditorToolMode toolMode)
    {
        _viewModel.Editor.ActiveToolMode = toolMode;
        UpdateEditorToolButtons();
        UpdateCropOverlay();
        UpdateEditorControlState();
    }

    private void EditorToolToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
        {
            return;
        }

        SetActiveTool(tag switch
        {
            "crop" => EditorToolMode.Crop,
            _ => EditorToolMode.Transform,
        });
    }

    private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > double.Epsilon)
        {
            RenderTimelineRuler();
        }
    }

    private void RenderTimelineRuler()
    {
        if (TimelineRulerCanvas is null)
        {
            return;
        }

        TimelineRulerCanvas.Children.Clear();

        var timelineWidth = GetTimelineCanvasWidth();
        TimelineRulerCanvas.Width = timelineWidth;
        TimelineRulerCanvas.Height = 28;

        if (GetTimelineDurationSeconds() <= 0)
        {
            return;
        }

        var pixelsPerSecond = GetTimelinePixelsPerSecond();
        var interval = GetTimelineMarkerInterval(pixelsPerSecond);
        var rulerBrush = TryFindResource("BorderBrushDark") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
        var textBrush = TryFindResource("MutedTextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
        var accentBrush = TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Goldenrod;
        var maxSeconds = GetTimelineDurationSeconds();

        var startTick = new System.Windows.Shapes.Rectangle
        {
            Width = 2,
            Height = 18,
            Fill = accentBrush,
            RadiusX = 1,
            RadiusY = 1,
        };

        Canvas.SetLeft(startTick, TimeToTimelineX(0) - (startTick.Width / 2d));
        Canvas.SetTop(startTick, 8);
        TimelineRulerCanvas.Children.Add(startTick);

        var startLabel = new TextBlock
        {
            Text = "Start",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = accentBrush,
        };

        Canvas.SetLeft(startLabel, TimeToTimelineX(0) + 6);
        Canvas.SetTop(startLabel, 0);
        TimelineRulerCanvas.Children.Add(startLabel);

        for (var seconds = 0d; seconds <= maxSeconds + 0.001d; seconds += interval)
        {
            var x = TimeToTimelineX(seconds);
            var tick = new System.Windows.Shapes.Rectangle
            {
                Width = seconds % (interval * 4d) == 0 ? 2 : 1,
                Height = seconds % (interval * 4d) == 0 ? 14 : 8,
                Fill = rulerBrush,
                RadiusX = 1,
                RadiusY = 1,
            };

            Canvas.SetLeft(tick, x - (tick.Width / 2d));
            Canvas.SetTop(tick, 14 - tick.Height);
            TimelineRulerCanvas.Children.Add(tick);

            if (seconds % (interval * 2d) < 0.0001d)
            {
                var label = new TextBlock
                {
                    Text = FormatTimelineTime(seconds),
                    FontSize = 11,
                    Foreground = textBrush,
                };

                Canvas.SetLeft(label, x + 4);
                Canvas.SetTop(label, 0);
                TimelineRulerCanvas.Children.Add(label);
            }
        }
    }

    private static double GetTimelineMarkerInterval(double pixelsPerSecond)
    {
        var candidates = new[] { 0.25d, 0.5d, 1d, 2d, 5d, 10d, 15d, 30d, 60d };
        return candidates.FirstOrDefault(candidate => candidate * pixelsPerSecond >= 72d, 60d);
    }

    private static string FormatTimelineTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }

    private void UpdateTransformControlsFromState()
    {
        if (RotationSlider is null || ScaleSlider is null || PositionXSlider is null || PositionYSlider is null || OpacitySlider is null)
        {
            return;
        }

        _isUpdatingTransformControls = true;
        RotationSlider.Value = _rotationDegrees;
        ScaleSlider.Value = _scalePercent;
        PositionXSlider.Value = _translateX;
        PositionYSlider.Value = _translateY;
        OpacitySlider.Value = _opacityPercent;
        RotationTextBox.Text = _rotationDegrees.ToString("0.##");
        ScaleTextBox.Text = _scalePercent.ToString("0.##");
        PositionXTextBox.Text = _translateX.ToString("0.##");
        PositionYTextBox.Text = _translateY.ToString("0.##");
        OpacityTextBox.Text = _opacityPercent.ToString("0.##");
        _isUpdatingTransformControls = false;

        if (CopySegmentSettingsButton is not null)
        {
            CopySegmentSettingsButton.IsEnabled = _selectedTimelineSegment is not null && !_isEditorBusy;
        }

        if (PasteSegmentSettingsButton is not null)
        {
            PasteSegmentSettingsButton.IsEnabled = _copiedSegmentSettings is not null && _selectedTimelineSegment is not null && !_isEditorBusy;
        }
    }

    private void ApplyTransformStateChange(Action mutation)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        mutation();
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTransformControlsFromState();
        UpdateEditorVisuals();
    }

    private void TransformSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingTransformControls || _selectedTimelineSegment is null)
        {
            return;
        }

        if (sender == RotationSlider)
        {
            _rotationDegrees = RotationSlider.Value;
        }
        else if (sender == ScaleSlider)
        {
            _scalePercent = ScaleSlider.Value;
        }
        else if (sender == PositionXSlider)
        {
            _translateX = PositionXSlider.Value;
        }
        else if (sender == PositionYSlider)
        {
            _translateY = PositionYSlider.Value;
        }
        else if (sender == OpacitySlider)
        {
            _opacityPercent = OpacitySlider.Value;
        }

        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTransformControlsFromState();
        UpdateEditorVisuals();
    }

    private void TransformSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CaptureTimelineUndoSnapshot();
    }

    private void TransformSlider_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var key = GetActualKey(e);

        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            CaptureTimelineUndoSnapshot();
        }
    }

    private void TransformTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTransformTextBoxValue(sender as WpfTextBox);
    }

    private void TransformTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var key = GetActualKey(e);

        if (key == Key.Enter)
        {
            CommitTransformTextBoxValue(sender as WpfTextBox);
            e.Handled = true;
        }
    }

    private void CommitTransformTextBoxValue(WpfTextBox? textBox)
    {
        if (textBox is null || _selectedTimelineSegment is null)
        {
            return;
        }

        if (!double.TryParse(textBox.Text, out var value))
        {
            UpdateTransformControlsFromState();
            return;
        }

        CaptureTimelineUndoSnapshot();

        if (textBox == RotationTextBox)
        {
            _rotationDegrees = Math.Clamp(value, -180d, 180d);
        }
        else if (textBox == ScaleTextBox)
        {
            _scalePercent = Math.Clamp(value, 10d, 400d);
        }
        else if (textBox == PositionXTextBox)
        {
            _translateX = Math.Clamp(value, -_selectedClipWidth, _selectedClipWidth);
        }
        else if (textBox == PositionYTextBox)
        {
            _translateY = Math.Clamp(value, -_selectedClipHeight, _selectedClipHeight);
        }
        else if (textBox == OpacityTextBox)
        {
            _opacityPercent = Math.Clamp(value, 1d, 100d);
        }

        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTransformControlsFromState();
        UpdateEditorVisuals();
    }

    private void CopySegmentSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCurrentEditorStateToSelectedSegment();

        if (_selectedTimelineSegment is null)
        {
            return;
        }

        _copiedSegmentSettings = _selectedTimelineSegment.CloneForSnapshot();
        _viewModel.EditorStatus = "Copied the selected segment settings.";
        UpdateTransformControlsFromState();
    }

    private void PasteSegmentSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null || _copiedSegmentSettings is null)
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        _selectedTimelineSegment.CopyVisualSettingsFrom(_copiedSegmentSettings);
        LoadSelectedSegmentIntoEditorState(_selectedTimelineSegment);
        UpdateEditorVisuals();
        _viewModel.EditorStatus = "Pasted settings onto the selected segment.";
    }

    private void DuplicateSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null || !TryGetSelectedSegmentIndex(out var index))
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        ApplyCurrentEditorStateToSelectedSegment();

        var duplicate = _selectedTimelineSegment.DuplicateForTimeline();
        _timelineSegments.Insert(index + 1, duplicate);
        NormalizeTimelineSegmentPositions();
        SelectTimelineSegment(duplicate, movePlayheadToSegmentStart: true);
        _viewModel.EditorStatus = "Duplicated the selected segment.";
    }

    private void ResetTransformButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTransformStateChange(() =>
        {
            _rotationDegrees = 0d;
            _scalePercent = 100d;
            _translateX = 0d;
            _translateY = 0d;
            _opacityPercent = 100d;
            _flipHorizontal = false;
            _flipVertical = false;
        });
    }

    private void FlipHorizontalButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTransformStateChange(() => _flipHorizontal = !_flipHorizontal);
    }

    private void FlipVerticalButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTransformStateChange(() => _flipVertical = !_flipVertical);
    }

    private void FitToFrameButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTransformStateChange(() => ApplyFitOrFillTransform(fillFrame: false));
    }

    private void FillFrameButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTransformStateChange(() => ApplyFitOrFillTransform(fillFrame: true));
    }

    private void ApplyFitOrFillTransform(bool fillFrame)
    {
        if (_selectedClipWidth <= 0 || _selectedClipHeight <= 0)
        {
            return;
        }

        _scalePercent = 100d;
        _translateX = 0d;
        _translateY = 0d;
    }

    private void StepBackButton_Click(object sender, RoutedEventArgs e)
    {
        StepPreviewFrames(-1);
    }

    private void StepForwardButton_Click(object sender, RoutedEventArgs e)
    {
        StepPreviewFrames(1);
    }

    private void StepPreviewFrames(int direction)
    {
        if (GetTimelineDurationSeconds() <= 0)
        {
            return;
        }

        PausePreviewPlayback();
        var frameDuration = 1d / Math.Max(1, int.TryParse(_viewModel.FpsTargetText, out var fps) ? fps : 30);
        _playheadSeconds = Math.Clamp(_playheadSeconds + (frameDuration * direction), 0, GetTimelineDurationSeconds());
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private double GetInterpolatedZoomMultiplier()
    {
        return 1d;
    }

    private TransformGroup BuildPreviewTransformGroup()
    {
        var previewScaleX = _displayedVideoRect.Width / Math.Max(1d, _selectedClipWidth);
        var previewScaleY = _displayedVideoRect.Height / Math.Max(1d, _selectedClipHeight);
        var zoomMultiplier = GetInterpolatedZoomMultiplier();

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(new ScaleTransform(
            (_scalePercent / 100d) * zoomMultiplier * (_flipHorizontal ? -1d : 1d),
            (_scalePercent / 100d) * zoomMultiplier * (_flipVertical ? -1d : 1d)));
        transformGroup.Children.Add(new RotateTransform(_rotationDegrees));
        transformGroup.Children.Add(new TranslateTransform(_translateX * previewScaleX, _translateY * previewScaleY));
        return transformGroup;
    }

    private void ApplyPreviewPresenterVisuals(Rect cropLocalRect)
    {
        if (PreviewVideoPresenter is null
            || PreviewMediaHost is null
            || TransformSelectionBorder is null
            || TransformMoveThumb is null
            || CropOverlayCanvas is null)
        {
            return;
        }

        var presenterLeft = _displayedVideoRect.X + cropLocalRect.X;
        var presenterTop = _displayedVideoRect.Y + cropLocalRect.Y;
        var presenterWidth = Math.Max(1d, cropLocalRect.Width);
        var presenterHeight = Math.Max(1d, cropLocalRect.Height);

        PreviewVideoPresenter.Width = presenterWidth;
        PreviewVideoPresenter.Height = presenterHeight;
        Canvas.SetLeft(PreviewVideoPresenter, presenterLeft);
        Canvas.SetTop(PreviewVideoPresenter, presenterTop);
        PreviewVideoPresenter.Clip = null;
        PreviewVideoPresenter.RenderTransform = Transform.Identity;
        PreviewVideoPresenter.Opacity = 1d;

        var transformGroup = BuildPreviewTransformGroup();

        PreviewMediaHost.Width = _displayedVideoRect.Width;
        PreviewMediaHost.Height = _displayedVideoRect.Height;
        PreviewMediaHost.Margin = new Thickness(-cropLocalRect.X, -cropLocalRect.Y, 0, 0);
        PreviewMediaHost.RenderTransformOrigin = new WpfPoint(
            Math.Clamp((_cropRectSource.X + (_cropRectSource.Width / 2d)) / Math.Max(1d, _selectedClipWidth), 0d, 1d),
            Math.Clamp((_cropRectSource.Y + (_cropRectSource.Height / 2d)) / Math.Max(1d, _selectedClipHeight), 0d, 1d));
        PreviewMediaHost.RenderTransform = transformGroup;
        PreviewMediaHost.Opacity = Math.Clamp(_opacityPercent / 100d, 0.01d, 1d);

        TransformSelectionBorder.Width = presenterWidth;
        TransformSelectionBorder.Height = presenterHeight;
        Canvas.SetLeft(TransformSelectionBorder, presenterLeft);
        Canvas.SetTop(TransformSelectionBorder, presenterTop);
        TransformSelectionBorder.RenderTransform = Transform.Identity;
        TransformSelectionBorder.Visibility = _selectedTimelineSegment is not null && _viewModel.Editor.IsTransformToolActive
            ? Visibility.Visible
            : Visibility.Collapsed;

        TransformMoveThumb.Width = presenterWidth;
        TransformMoveThumb.Height = presenterHeight;
        Canvas.SetLeft(TransformMoveThumb, presenterLeft);
        Canvas.SetTop(TransformMoveThumb, presenterTop);
        TransformMoveThumb.RenderTransform = Transform.Identity;
        TransformMoveThumb.Visibility = _selectedTimelineSegment is not null && _viewModel.Editor.IsTransformToolActive
            ? Visibility.Visible
            : Visibility.Collapsed;

        CropOverlayCanvas.Width = presenterWidth;
        CropOverlayCanvas.Height = presenterHeight;
        Canvas.SetLeft(CropOverlayCanvas, presenterLeft);
        Canvas.SetTop(CropOverlayCanvas, presenterTop);
        CropOverlayCanvas.RenderTransform = Transform.Identity;

        PositionCropShades(cropLocalRect);
    }

    private void PositionCropShades(Rect cropLocalRect)
    {
        if (CropShadeTop is null || CropShadeLeft is null || CropShadeRight is null || CropShadeBottom is null || CropOverlayCanvas is null)
        {
            return;
        }

        CropShadeTop.Visibility = Visibility.Collapsed;
        CropShadeLeft.Visibility = Visibility.Collapsed;
        CropShadeRight.Visibility = Visibility.Collapsed;
        CropShadeBottom.Visibility = Visibility.Collapsed;
    }

    private void TransformMoveThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        CaptureTimelineUndoSnapshot();
    }

    private void TransformMoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!TryGetPreviewSourceDelta(e.HorizontalChange, e.VerticalChange, out var deltaX, out var deltaY))
        {
            return;
        }

        if (Math.Abs(e.HorizontalChange) < PreviewMoveDeadzonePixels && Math.Abs(e.VerticalChange) < PreviewMoveDeadzonePixels)
        {
            return;
        }

        _translateX = Math.Clamp(_translateX + deltaX, -_selectedClipWidth, _selectedClipWidth);
        _translateY = Math.Clamp(_translateY + deltaY, -_selectedClipHeight, _selectedClipHeight);
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTransformControlsFromState();
        UpdateEditorVisuals();
    }
}
