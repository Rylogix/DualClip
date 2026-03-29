using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DualClip.Core.Models;

namespace DualClip.Infrastructure;

public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly Dictionary<int, HotkeyRegistration> _registrations = new();
    private HwndSource? _hwndSource;
    private nint _windowHandle;
    private int _nextId = 0x5000;

    public event EventHandler<string>? HotkeyPressed;

    public void Attach(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("The window handle was not ready yet.", nameof(windowHandle));
        }

        Detach();

        _windowHandle = windowHandle;
        _hwndSource = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("Could not access the window message source.");
        _hwndSource.AddHook(WndProc);
    }

    public void Register(string registrationName, HotkeyGesture gesture)
    {
        if (!gesture.IsEnabled)
        {
            return;
        }

        EnsureAttached();

        var registrationId = _nextId++;
        var modifiers = (uint)(gesture.Modifiers | HotkeyModifiers.NoRepeat);

        if (!RegisterHotKey(_windowHandle, registrationId, modifiers, gesture.VirtualKey))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to register hotkey '{registrationName}'. Another application may already be using it.");
        }

        _registrations.Add(registrationId, new HotkeyRegistration(registrationId, registrationName, CloneGesture(gesture)));
    }

    public void ReplaceAll(IEnumerable<KeyValuePair<string, HotkeyGesture>> registrations)
    {
        UnregisterAll();

        foreach (var registration in registrations)
        {
            Register(registration.Key, registration.Value);
        }
    }

    public void RefreshAll()
    {
        EnsureAttached();

        foreach (var registration in _registrations.Values.ToArray())
        {
            if (!UnregisterHotKey(_windowHandle, registration.Id))
            {
                continue;
            }

            var modifiers = (uint)(registration.Gesture.Modifiers | HotkeyModifiers.NoRepeat);

            if (!RegisterHotKey(_windowHandle, registration.Id, modifiers, registration.Gesture.VirtualKey))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Failed to refresh hotkey '{registration.Name}'. Another application may already be using it.");
            }
        }
    }

    public void UnregisterAll()
    {
        if (_windowHandle == nint.Zero)
        {
            _registrations.Clear();
            return;
        }

        foreach (var registrationId in _registrations.Keys.ToArray())
        {
            UnregisterHotKey(_windowHandle, registrationId);
        }

        _registrations.Clear();
    }

    public void Detach()
    {
        UnregisterAll();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _windowHandle = nint.Zero;
    }

    public void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }

    private void EnsureAttached()
    {
        if (_windowHandle == nint.Zero || _hwndSource is null)
        {
            throw new InvalidOperationException("Attach the hotkey manager to the main window before registering hotkeys.");
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registrations.TryGetValue(wParam.ToInt32(), out var registration))
        {
            HotkeyPressed?.Invoke(this, registration.Name);
            handled = true;
        }

        return nint.Zero;
    }

    private static HotkeyGesture CloneGesture(HotkeyGesture gesture)
    {
        return new HotkeyGesture
        {
            VirtualKey = gesture.VirtualKey,
            Modifiers = gesture.Modifiers,
        };
    }

    private sealed record HotkeyRegistration(int Id, string Name, HotkeyGesture Gesture);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
