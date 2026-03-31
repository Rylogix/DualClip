using System.IO;
using System.Text;
using DualClip.Infrastructure;

namespace DualClip.App;

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(AppPaths.AppDataRoot, "startup.log");

    public static void Write(string message)
    {
        AppLog.Info("Startup", message);

        try
        {
            Directory.CreateDirectory(AppPaths.AppDataRoot);

            lock (Sync)
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                    global::System.Text.Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
