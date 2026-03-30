using DualClip.Core.Models;

namespace DualClip.App;

public sealed class MonitorNodeViewModel : BindableObject
{
    private string _name;
    private MonitorDescriptor? _selectedMonitor;
    private string _outputFolder;
    private string _status;
    private bool _isCapturing;

    public MonitorNodeViewModel(string id, string name, string outputFolder)
    {
        Id = id;
        _name = name;
        _outputFolder = outputFolder;
        _status = $"{DisplayTitle} idle.";
        Hotkey = new HotkeyEditorState("Monitor node");
    }

    public string Id { get; }

    public HotkeyEditorState Hotkey { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                RaisePropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? "Monitor Node" : Name.Trim();

    public MonitorDescriptor? SelectedMonitor
    {
        get => _selectedMonitor;
        set => SetProperty(ref _selectedMonitor, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set => SetProperty(ref _outputFolder, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                RaisePropertyChanged(nameof(CanStartClipping));
                RaisePropertyChanged(nameof(CanStopClipping));
                RaisePropertyChanged(nameof(CanSaveClip));
            }
        }
    }

    public bool CanStartClipping => !IsCapturing;

    public bool CanStopClipping => IsCapturing;

    public bool CanSaveClip => IsCapturing;

    public void LoadHotkey(HotkeyGesture hotkey)
    {
        Hotkey.Load(hotkey);
    }
}
