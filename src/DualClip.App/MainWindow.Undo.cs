using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DualClip.App;

public partial class MainWindow
{
    private const int MaxTimelineUndoSnapshots = 100;
    private readonly List<TimelineUndoSnapshot> _timelineUndoSnapshots = [];
    private bool _isRestoringTimelineUndo;

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var key = GetActualKey(e);
        var modifiers = Keyboard.Modifiers;

        if (IsAnyHotkeyEditorRecording())
        {
            return;
        }

        if (key == Key.Z
            && modifiers == ModifierKeys.Control
            && (!IsTextInputFocused() || IsFocusedElementTransformInput())
            && PrepareFocusedTransformUndo()
            && TryUndoTimelineEdit())
        {
            e.Handled = true;
            return;
        }

        if (IsTextInputFocused())
        {
            return;
        }

        if (_isEditorBusy)
        {
            return;
        }

        if (key == Key.Space && modifiers == ModifierKeys.None)
        {
            if (_isTimelinePlaybackActive)
            {
                PausePreviewPlayback();
                SeekToPlayhead(updatePreviewPosition: true);
            }
            else
            {
                PlayPreviewButton_Click(this, new RoutedEventArgs());
            }

            e.Handled = true;
            return;
        }

        if (key == Key.X && modifiers == ModifierKeys.None)
        {
            SplitSegmentButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (key == Key.Delete && modifiers == ModifierKeys.None)
        {
            DeleteSegmentButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (key == Key.R && modifiers == ModifierKeys.None)
        {
            ResetTransformButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (key == Key.C && modifiers == ModifierKeys.None)
        {
            CopySegmentButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (key == Key.V && modifiers == ModifierKeys.None)
        {
            PasteSegmentButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void EditorMutationThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        CaptureTimelineUndoSnapshot();
    }

    private void ZoomKeyframeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CaptureTimelineUndoSnapshot();
    }

    private void ZoomKeyframeSlider_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var key = GetActualKey(e);

        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            CaptureTimelineUndoSnapshot();
        }
    }

    private void CaptureTimelineUndoSnapshot()
    {
        if (_isRestoringTimelineUndo || _isEditorBusy)
        {
            return;
        }

        var selectedClip = GetSelectedClip();

        if (selectedClip is null || _selectedClipWidth <= 0 || _selectedClipHeight <= 0)
        {
            return;
        }

        ApplyCurrentEditorStateToSelectedSegment();

        _timelineUndoSnapshots.Add(new TimelineUndoSnapshot
        {
            ClipFilePath = selectedClip.FilePath,
            PlayheadSeconds = _playheadSeconds,
            SelectedSegmentId = _selectedTimelineSegment?.Id,
            CopiedSegment = _copiedTimelineSegment?.CloneForSnapshot(),
            Segments = _timelineSegments.Select(segment => segment.CloneForSnapshot()).ToList(),
        });

        if (_timelineUndoSnapshots.Count > MaxTimelineUndoSnapshots)
        {
            _timelineUndoSnapshots.RemoveAt(0);
        }
    }

    private void ClearTimelineUndoHistory()
    {
        _timelineUndoSnapshots.Clear();
    }

    private bool TryUndoTimelineEdit()
    {
        if (_isEditorBusy || _timelineUndoSnapshots.Count == 0)
        {
            return false;
        }

        var selectedClip = GetSelectedClip();

        if (selectedClip is null)
        {
            ClearTimelineUndoHistory();
            return false;
        }

        var snapshot = _timelineUndoSnapshots[^1];

        if (!string.Equals(snapshot.ClipFilePath, selectedClip.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ClearTimelineUndoHistory();
            return false;
        }

        _timelineUndoSnapshots.RemoveAt(_timelineUndoSnapshots.Count - 1);
        RestoreTimelineUndoSnapshot(snapshot);
        _viewModel.EditorStatus = "Undid the last timeline edit.";
        return true;
    }

    private void RestoreTimelineUndoSnapshot(TimelineUndoSnapshot snapshot)
    {
        _isRestoringTimelineUndo = true;

        try
        {
            PausePreviewPlayback();
            _timelineSegments.Clear();

            foreach (var segment in snapshot.Segments)
            {
                _timelineSegments.Add(segment.CloneForSnapshot());
            }

            NormalizeTimelineSegmentPositions();

            _copiedTimelineSegment = snapshot.CopiedSegment?.CloneForSnapshot();
            _selectedTimelineSegment = snapshot.SelectedSegmentId is Guid selectedId
                ? _timelineSegments.FirstOrDefault(segment => segment.Id == selectedId)
                : null;

            _playheadSeconds = Math.Clamp(snapshot.PlayheadSeconds, 0, _timelineSegments.Sum(segment => segment.DurationSeconds));

            if (_selectedTimelineSegment is not null)
            {
                LoadSelectedSegmentIntoEditorState(_selectedTimelineSegment);
            }
            else
            {
                _trimStartSeconds = 0;
                _trimEndSeconds = 0;
                _zoomKeyframe1Seconds = 0;
                _zoomKeyframe2Seconds = 0;
                _zoomKeyframe1Percent = 100d;
                _zoomKeyframe2Percent = 100d;
                _cropRectSource = Rect.Empty;
                UpdateZoomSlidersFromState();
            }

            SeekToPlayhead(updatePreviewPosition: true);
            UpdateEditorVisuals();
        }
        finally
        {
            _isRestoringTimelineUndo = false;
        }
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.ComboBox
            or System.Windows.Controls.ComboBoxItem
            or System.Windows.Controls.Slider
            or System.Windows.Controls.CheckBox
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.Primitives.TextBoxBase;
    }

    private bool IsFocusedElementTransformInput()
    {
        return Keyboard.FocusedElement is Slider slider && IsTransformSlider(slider)
            || Keyboard.FocusedElement is System.Windows.Controls.TextBox textBox && IsTransformTextBox(textBox);
    }

    private bool PrepareFocusedTransformUndo()
    {
        if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox textBox || !IsTransformTextBox(textBox))
        {
            return true;
        }

        if (!double.TryParse(textBox.Text, out _))
        {
            UpdateTransformControlsFromState();
            return false;
        }

        CommitTransformTextBoxValue(textBox);
        return true;
    }

    private bool IsTransformSlider(Slider slider)
    {
        return ReferenceEquals(slider, RotationSlider)
            || ReferenceEquals(slider, ScaleSlider)
            || ReferenceEquals(slider, PositionXSlider)
            || ReferenceEquals(slider, PositionYSlider)
            || ReferenceEquals(slider, OpacitySlider);
    }

    private bool IsTransformTextBox(System.Windows.Controls.TextBox textBox)
    {
        return ReferenceEquals(textBox, RotationTextBox)
            || ReferenceEquals(textBox, ScaleTextBox)
            || ReferenceEquals(textBox, PositionXTextBox)
            || ReferenceEquals(textBox, PositionYTextBox)
            || ReferenceEquals(textBox, OpacityTextBox);
    }

    private sealed class TimelineUndoSnapshot
    {
        public required string ClipFilePath { get; init; }

        public required double PlayheadSeconds { get; init; }

        public required Guid? SelectedSegmentId { get; init; }

        public required TimelineSegment? CopiedSegment { get; init; }

        public required List<TimelineSegment> Segments { get; init; }
    }
}
