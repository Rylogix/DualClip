using System.Collections.ObjectModel;

namespace DualClip.App;

public sealed class EditorSurfaceState : BindableObject
{
    private EditorToolMode _activeToolMode = EditorToolMode.Transform;
    private double _timelineZoomPercent = 100d;
    private bool _isSnappingEnabled = true;
    private bool _isRippleDeleteEnabled = true;
    private bool _isCropAspectLocked;
    private SelectionOption<string>? _selectedCropAspectRatio;
    private string _selectedClipTitle = "No clip selected";

    public EditorSurfaceState()
    {
        CropAspectRatios.Add(new SelectionOption<string> { Label = "Free", Value = "free" });
        CropAspectRatios.Add(new SelectionOption<string> { Label = "Source", Value = "source" });
        CropAspectRatios.Add(new SelectionOption<string> { Label = "16:9", Value = "16:9" });
        CropAspectRatios.Add(new SelectionOption<string> { Label = "9:16", Value = "9:16" });
        CropAspectRatios.Add(new SelectionOption<string> { Label = "1:1", Value = "1:1" });
        CropAspectRatios.Add(new SelectionOption<string> { Label = "4:3", Value = "4:3" });
        SelectedCropAspectRatio = CropAspectRatios[0];
    }

    public ObservableCollection<SelectionOption<string>> CropAspectRatios { get; } = [];

    public EditorToolMode ActiveToolMode
    {
        get => _activeToolMode;
        set
        {
            if (SetProperty(ref _activeToolMode, value))
            {
                RaisePropertyChanged(nameof(IsSelectToolActive));
                RaisePropertyChanged(nameof(IsCropToolActive));
                RaisePropertyChanged(nameof(IsTransformToolActive));
            }
        }
    }

    public bool IsSelectToolActive => ActiveToolMode == EditorToolMode.Select;

    public bool IsCropToolActive => ActiveToolMode == EditorToolMode.Crop;

    public bool IsTransformToolActive => ActiveToolMode == EditorToolMode.Transform;

    public double TimelineZoomPercent
    {
        get => _timelineZoomPercent;
        set => SetProperty(ref _timelineZoomPercent, value);
    }

    public bool IsSnappingEnabled
    {
        get => _isSnappingEnabled;
        set => SetProperty(ref _isSnappingEnabled, value);
    }

    public bool IsRippleDeleteEnabled
    {
        get => _isRippleDeleteEnabled;
        set => SetProperty(ref _isRippleDeleteEnabled, value);
    }

    public bool IsCropAspectLocked
    {
        get => _isCropAspectLocked;
        set => SetProperty(ref _isCropAspectLocked, value);
    }

    public SelectionOption<string>? SelectedCropAspectRatio
    {
        get => _selectedCropAspectRatio;
        set => SetProperty(ref _selectedCropAspectRatio, value);
    }

    public string SelectedClipTitle
    {
        get => _selectedClipTitle;
        set => SetProperty(ref _selectedClipTitle, value);
    }
}
