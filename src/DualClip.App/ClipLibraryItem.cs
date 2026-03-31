using System.IO;

namespace DualClip.App;

public sealed class ClipLibraryItem : BindableObject
{
    private string _displayName = string.Empty;
    private DateTime _modifiedAt;
    private long _fileSizeBytes;
    private string _editableFileName = string.Empty;
    private bool _isRenaming;
    private bool _isProcessing;
    private string _thumbnailPath = string.Empty;

    public required string FilePath { get; init; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public DateTime ModifiedAt
    {
        get => _modifiedAt;
        private set
        {
            if (SetProperty(ref _modifiedAt, value))
            {
                RaisePropertyChanged(nameof(ModifiedDisplay));
                RaisePropertyChanged(nameof(MetadataText));
            }
        }
    }

    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        private set
        {
            if (SetProperty(ref _fileSizeBytes, value))
            {
                RaisePropertyChanged(nameof(FileSizeDisplay));
                RaisePropertyChanged(nameof(MetadataText));
            }
        }
    }

    public string FileName => Path.GetFileName(FilePath);

    public string FileExtension => Path.GetExtension(FilePath);

    public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(FilePath);

    public string ModifiedDisplay => ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string FileSizeDisplay => FormatFileSize(FileSizeBytes);

    public string MetadataText => $"{ModifiedDisplay}   {FileSizeDisplay}";

    public string EditableFileName
    {
        get => _editableFileName;
        set => SetProperty(ref _editableFileName, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    public string ThumbnailPath
    {
        get => _thumbnailPath;
        private set
        {
            if (SetProperty(ref _thumbnailPath, value))
            {
                RaisePropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailPath);

    public void SetThumbnailPath(string? thumbnailPath)
    {
        ThumbnailPath = thumbnailPath ?? string.Empty;
    }

    public void BeginRename()
    {
        EditableFileName = FileNameWithoutExtension;
        IsRenaming = true;
    }

    public void CancelRename()
    {
        EditableFileName = FileNameWithoutExtension;
        IsRenaming = false;
    }

    public void EndRename()
    {
        IsRenaming = false;
    }

    public void UpdateFileDetails(string displayName, DateTime modifiedAt, long fileSizeBytes)
    {
        DisplayName = displayName;
        ModifiedAt = modifiedAt;
        FileSizeBytes = fileSizeBytes;

        if (!IsRenaming)
        {
            EditableFileName = FileNameWithoutExtension;
        }
    }

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
