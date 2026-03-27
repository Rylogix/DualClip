using System.IO;

namespace DualClip.App;

public sealed class ClipLibraryItem
{
    public required string FilePath { get; init; }

    public required string DisplayName { get; init; }

    public required DateTime ModifiedAt { get; init; }

    public required long FileSizeBytes { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public string ModifiedDisplay => ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string FileSizeDisplay => FormatFileSize(FileSizeBytes);

    public string MetadataText => $"{ModifiedDisplay}   {FileSizeDisplay}";

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var suffixIndex = 0;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        var format = size >= 100 || suffixIndex == 0 ? "0" : "0.0";
        return $"{size.ToString(format)} {suffixes[suffixIndex]}";
    }
}
