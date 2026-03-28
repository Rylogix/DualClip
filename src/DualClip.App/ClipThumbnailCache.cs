using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DualClip.Infrastructure;

namespace DualClip.App;

public sealed class ClipThumbnailCache
{
    private const string ThumbnailDirectoryName = "clip-thumbnails";

    public async Task<string?> EnsureThumbnailAsync(
        string ffmpegPath,
        ClipLibraryItem clip,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)
            || !File.Exists(ffmpegPath)
            || string.IsNullOrWhiteSpace(clip.FilePath)
            || !File.Exists(clip.FilePath))
        {
            return null;
        }

        var thumbnailPath = GetThumbnailPath(clip);

        if (File.Exists(thumbnailPath))
        {
            return thumbnailPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(thumbnailPath)!);

        try
        {
            await GenerateThumbnailAsync(ffmpegPath, clip.FilePath, thumbnailPath, cancellationToken).ConfigureAwait(false);
            return File.Exists(thumbnailPath) ? thumbnailPath : null;
        }
        catch
        {
            TryDeleteFile(thumbnailPath);
            return null;
        }
    }

    private static string GetThumbnailPath(ClipLibraryItem clip)
    {
        var cacheRoot = Path.Combine(AppPaths.AppDataRoot, ThumbnailDirectoryName);
        var cacheKey = $"{clip.FilePath}|{clip.ModifiedAt.ToUniversalTime().Ticks}|{clip.FileSizeBytes}";
        var fileName = $"{ComputeHash(cacheKey)}.jpg";
        return Path.Combine(cacheRoot, fileName);
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task GenerateThumbnailAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(inputPath);
        process.StartInfo.ArgumentList.Add("-vf");
        process.StartInfo.ArgumentList.Add("thumbnail=60,scale=640:-1:force_original_aspect_ratio=decrease");
        process.StartInfo.ArgumentList.Add("-frames:v");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add(outputPath);

        if (!process.Start())
        {
            return;
        }

        _ = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDeleteFile(outputPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
