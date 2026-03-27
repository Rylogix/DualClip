using System.Windows.Input;

namespace DualClip.App;

public static class HotkeyCatalog
{
    public static IReadOnlyList<HotkeyKeyOption> AllKeys { get; } = BuildOptions();

    public static string GetDisplayName(uint virtualKey)
    {
        var knownName = AllKeys.FirstOrDefault(option => option.VirtualKey == virtualKey)?.DisplayName;

        if (!string.IsNullOrWhiteSpace(knownName))
        {
            return knownName;
        }

        return KeyInterop.KeyFromVirtualKey((int)virtualKey) switch
        {
            Key.Return => "Enter",
            Key.Space => "Space",
            Key.Escape => "Escape",
            Key.Prior => "Page Up",
            Key.Next => "Page Down",
            Key.Back => "Backspace",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            var key when key != Key.None => key.ToString().Replace("NumPad", "NumPad "),
            _ => $"VK {virtualKey}",
        };
    }

    private static IReadOnlyList<HotkeyKeyOption> BuildOptions()
    {
        var options = new List<HotkeyKeyOption>
        {
            new() { VirtualKey = 0, DisplayName = "Disabled" },
        };

        for (uint key = 0x70; key <= 0x87; key++)
        {
            options.Add(new HotkeyKeyOption { VirtualKey = key, DisplayName = $"F{key - 0x6F}" });
        }

        for (uint key = 0x30; key <= 0x39; key++)
        {
            options.Add(new HotkeyKeyOption { VirtualKey = key, DisplayName = ((char)key).ToString() });
        }

        for (uint key = 0x41; key <= 0x5A; key++)
        {
            options.Add(new HotkeyKeyOption { VirtualKey = key, DisplayName = ((char)key).ToString() });
        }

        var extraKeys = new Dictionary<uint, string>
        {
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x24] = "Home",
            [0x23] = "End",
            [0x21] = "Page Up",
            [0x22] = "Page Down",
            [0x60] = "NumPad 0",
            [0x61] = "NumPad 1",
            [0x62] = "NumPad 2",
            [0x63] = "NumPad 3",
            [0x64] = "NumPad 4",
            [0x65] = "NumPad 5",
            [0x66] = "NumPad 6",
            [0x67] = "NumPad 7",
            [0x68] = "NumPad 8",
            [0x69] = "NumPad 9",
        };

        options.AddRange(extraKeys.Select(pair => new HotkeyKeyOption
        {
            VirtualKey = pair.Key,
            DisplayName = pair.Value,
        }));

        return options;
    }
}
