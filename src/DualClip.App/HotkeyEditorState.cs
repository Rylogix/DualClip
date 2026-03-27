using DualClip.Core.Models;

namespace DualClip.App;

public sealed class HotkeyEditorState : BindableObject
{
    private HotkeyGesture _gesture = HotkeyGesture.Disabled();
    private HotkeyModifiers _pendingModifiers;
    private bool _isRecording;

    public HotkeyEditorState(string label)
    {
        Label = label;
    }

    public string Label { get; }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                RaisePropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string DisplayText
    {
        get
        {
            if (IsRecording)
            {
                return _pendingModifiers == HotkeyModifiers.None
                    ? "Press hotkey combination..."
                    : $"{FormatModifiers(_pendingModifiers)} + ...";
            }

            return _gesture.ToDisplayString(HotkeyCatalog.GetDisplayName);
        }
    }

    public void Load(HotkeyGesture gesture)
    {
        _gesture = new HotkeyGesture
        {
            VirtualKey = gesture.VirtualKey,
            Modifiers = gesture.Modifiers,
        };

        _pendingModifiers = HotkeyModifiers.None;
        _isRecording = false;
        RaisePropertyChanged(nameof(IsRecording));
        RaisePropertyChanged(nameof(DisplayText));
    }

    public void BeginRecording()
    {
        _pendingModifiers = HotkeyModifiers.None;
        IsRecording = true;
    }

    public void EndRecording()
    {
        _pendingModifiers = HotkeyModifiers.None;
        IsRecording = false;
    }

    public void SetPendingModifiers(HotkeyModifiers modifiers)
    {
        if (_pendingModifiers == modifiers)
        {
            return;
        }

        _pendingModifiers = modifiers;
        RaisePropertyChanged(nameof(DisplayText));
    }

    public void Capture(uint virtualKey, HotkeyModifiers modifiers)
    {
        _gesture = new HotkeyGesture
        {
            VirtualKey = virtualKey,
            Modifiers = modifiers,
        };

        _pendingModifiers = HotkeyModifiers.None;
        _isRecording = false;
        RaisePropertyChanged(nameof(IsRecording));
        RaisePropertyChanged(nameof(DisplayText));
    }

    public void Clear()
    {
        _gesture = HotkeyGesture.Disabled();
        _pendingModifiers = HotkeyModifiers.None;
        _isRecording = false;
        RaisePropertyChanged(nameof(IsRecording));
        RaisePropertyChanged(nameof(DisplayText));
    }

    public HotkeyGesture ToModel()
    {
        return new HotkeyGesture
        {
            VirtualKey = _gesture.VirtualKey,
            Modifiers = _gesture.Modifiers,
        };
    }

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        return string.Join(" + ", parts);
    }
}
