using System.IO;
using Microsoft.Win32;

namespace DualClip.App;

internal sealed class StartupLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DualClip";

    public void Apply(bool launchOnStartup)
    {
        if (launchOnStartup)
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("DualClip could not open the Windows startup registry key.");
            runKey.SetValue(ValueName, BuildCommandLine(), RegistryValueKind.String);
            return;
        }

        using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string BuildCommandLine()
    {
        var processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            throw new InvalidOperationException("DualClip could not determine its current executable path for startup launch.");
        }

        return $"\"{processPath}\"";
    }
}
