using System.IO;

namespace DualClip.Infrastructure;

public static class AppPaths
{
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualClip");

    public static string ConfigPath => Path.Combine(AppDataRoot, "config.json");

    public static string BufferRoot => Path.Combine(AppDataRoot, "buffers");

    public static string UpdatesRoot => Path.Combine(AppDataRoot, "updates");

    public static string GetBufferDirectory(string slotName) => Path.Combine(BufferRoot, slotName);

    public static string GetUpdateDirectory(string versionText)
    {
        var safeVersion = string.IsNullOrWhiteSpace(versionText)
            ? "unknown"
            : string.Concat(versionText.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

        return Path.Combine(UpdatesRoot, safeVersion);
    }

    public static string ResolveDefaultFfmpegPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);

        for (var index = 0; index < 8 && current is not null; index++)
        {
            candidates.Add(Path.Combine(current.FullName, "Tools", "ffmpeg.exe"));
            current = current.Parent;
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        try
        {
            var extractedPath = BundledToolExtractor.EnsureFfmpegExtracted();

            if (File.Exists(extractedPath))
            {
                return extractedPath;
            }
        }
        catch
        {
        }

        return candidates[0];
    }
}
