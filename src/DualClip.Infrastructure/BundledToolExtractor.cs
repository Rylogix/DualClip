using System.IO;
using System.Reflection;

namespace DualClip.Infrastructure;

public static class BundledToolExtractor
{
    private const string FfmpegResourceName = "DualClip.Infrastructure.Tools.ffmpeg.exe";

    public static string EnsureFfmpegExtracted()
    {
        var toolsDirectory = Path.Combine(AppPaths.AppDataRoot, "Tools");
        var targetPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
        Directory.CreateDirectory(toolsDirectory);

        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FfmpegResourceName)
            ?? throw new InvalidOperationException($"Bundled resource '{FfmpegResourceName}' was not found.");

        if (File.Exists(targetPath))
        {
            var targetInfo = new FileInfo(targetPath);

            if (targetInfo.Length == resourceStream.Length)
            {
                return targetPath;
            }
        }

        var temporaryPath = $"{targetPath}.tmp";

        using (var output = File.Create(temporaryPath))
        {
            resourceStream.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(temporaryPath, targetPath);
        return targetPath;
    }
}
