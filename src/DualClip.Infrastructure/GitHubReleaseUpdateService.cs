using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DualClip.Infrastructure;

public sealed class GitHubReleaseUpdateService
{
    private const string RepositoryOwner = "Rylogix";
    private const string RepositoryName = "DualClip";
    private const string LatestReleaseEndpoint = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] PreferredAssetNames = ["DualClip.App.exe", "DualClip.exe"];

    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService()
        : this(CreateHttpClient())
    {
    }

    internal GitHubReleaseUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        CurrentVersion = ResolveCurrentVersion();
        CurrentVersionText = ResolveCurrentVersionText(CurrentVersion);
        CurrentExecutablePath = ResolveCurrentExecutablePath();
    }

    public Version CurrentVersion { get; }

    public string CurrentVersionText { get; }

    public string CurrentExecutablePath { get; }

    public string ReleasesUrl => $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";

    public async Task<GitHubUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitHubUpdateCheckResult(
                IsUpdateAvailable: false,
                Release: null,
                StatusMessage: "No GitHub releases are published yet.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty release payload.");

        if (release.Draft || release.Prerelease)
        {
            return new GitHubUpdateCheckResult(
                IsUpdateAvailable: false,
                Release: null,
                StatusMessage: "No stable GitHub release is available yet.");
        }

        if (!TryParseVersion(release.TagName, out var releaseVersion))
        {
            throw new InvalidOperationException(
                $"GitHub release tag '{release.TagName ?? "<missing>"}' is not a supported semantic version. Use tags like v0.2.0.");
        }

        var asset = SelectAsset(release.Assets);

        if (asset is null)
        {
            return new GitHubUpdateCheckResult(
                IsUpdateAvailable: false,
                Release: null,
                StatusMessage: $"GitHub release {release.TagName} is missing a portable .exe asset for DualClip.");
        }

        var releaseInfo = new GitHubUpdateRelease(
            Version: releaseVersion,
            VersionText: NormalizeVersionText(release.TagName, releaseVersion),
            TagName: release.TagName ?? $"v{releaseVersion}",
            DisplayName: string.IsNullOrWhiteSpace(release.Name) ? release.TagName ?? $"v{releaseVersion}" : release.Name.Trim(),
            PublishedAtUtc: release.PublishedAt,
            HtmlUrl: release.HtmlUrl ?? $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/tag/{Uri.EscapeDataString(release.TagName ?? $"v{releaseVersion}")}",
            ReleaseNotes: NormalizeReleaseNotes(release.Body),
            AssetName: asset.Name!,
            AssetDownloadUrl: asset.BrowserDownloadUrl!);

        if (releaseVersion <= CurrentVersion)
        {
            return new GitHubUpdateCheckResult(
                IsUpdateAvailable: false,
                Release: releaseInfo,
                StatusMessage: $"DualClip is up to date on v{CurrentVersionText}.");
        }

        var publishedAtText = releaseInfo.PublishedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var statusMessage = string.IsNullOrWhiteSpace(publishedAtText)
            ? $"Update v{releaseInfo.VersionText} is available from GitHub."
            : $"Update v{releaseInfo.VersionText} is available from GitHub. Published {publishedAtText}.";

        return new GitHubUpdateCheckResult(
            IsUpdateAvailable: true,
            Release: releaseInfo,
            StatusMessage: statusMessage);
    }

    public async Task<GitHubPreparedUpdate> DownloadUpdateAsync(
        GitHubUpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        EnsureWritableInstallLocation();

        var updateDirectory = AppPaths.GetUpdateDirectory(release.VersionText);
        var stagingDirectory = Path.Combine(updateDirectory, DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(stagingDirectory);

        var downloadPath = Path.Combine(stagingDirectory, release.AssetName);

        try
        {
            await DownloadReleaseAssetAsync(release.AssetDownloadUrl, downloadPath, progress, cancellationToken);
            return new GitHubPreparedUpdate(release, stagingDirectory, downloadPath);
        }
        catch
        {
            TryDeleteFile(downloadPath);
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public GitHubPreparedUpdate? TryGetPreparedUpdate(GitHubUpdateRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        var updateDirectory = AppPaths.GetUpdateDirectory(release.VersionText);

        if (!Directory.Exists(updateDirectory))
        {
            return null;
        }

        var stagedDirectory = Directory
            .EnumerateDirectories(updateDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
            {
                var assetPath = Path.Combine(path, release.AssetName);
                return File.Exists(assetPath) && new FileInfo(assetPath).Length > 0;
            });

        if (string.IsNullOrWhiteSpace(stagedDirectory))
        {
            return null;
        }

        return new GitHubPreparedUpdate(
            release,
            stagedDirectory,
            Path.Combine(stagedDirectory, release.AssetName));
    }

    public void LaunchUpdaterAndRestart(GitHubPreparedUpdate preparedUpdate)
    {
        ArgumentNullException.ThrowIfNull(preparedUpdate);
        EnsureWritableInstallLocation();

        var backupPath = BuildBackupExecutablePath(CurrentExecutablePath);
        var scriptPath = Path.Combine(preparedUpdate.StagingDirectory, "apply-update.cmd");
        File.WriteAllText(scriptPath, BuildUpdateScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo
        {
            FileName = scriptPath,
            Arguments =
                $"\"{CurrentExecutablePath}\" \"{preparedUpdate.DownloadedAssetPath}\" \"{backupPath}\" {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}",
            WorkingDirectory = preparedUpdate.StagingDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        var updaterProcess = Process.Start(startInfo);
        if (updaterProcess is null)
        {
            throw new InvalidOperationException("DualClip could not start the updater helper process.");
        }
    }

    public void EnsureWritableInstallLocation()
    {
        var installDirectory = Path.GetDirectoryName(CurrentExecutablePath);

        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            throw new InvalidOperationException("DualClip could not determine its install folder for self-update.");
        }

        var probePath = Path.Combine(installDirectory, $".dualclip-update-{Guid.NewGuid():N}.tmp");

        try
        {
            using var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"DualClip cannot write to '{installDirectory}'. Move the app to a writable folder or update manually from {ReleasesUrl}.",
                ex);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch
            {
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DualClip-Updater");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        return httpClient;
    }

    private async Task DownloadReleaseAssetAsync(
        string assetDownloadUrl,
        string downloadPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, assetDownloadUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(downloadPath);

                var totalLength = response.Content.Headers.ContentLength;
                var buffer = new byte[81920];
                long bytesReadTotal = 0;

                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesReadTotal += read;

                    if (totalLength is > 0)
                    {
                        progress?.Report((double)bytesReadTotal / totalLength.Value);
                    }
                }

                await target.FlushAsync(cancellationToken);

                if (totalLength is > 0 && bytesReadTotal != totalLength.Value)
                {
                    throw new InvalidOperationException(
                        $"GitHub returned an incomplete update download. Expected {totalLength.Value} bytes but received {bytesReadTotal}.");
                }

                progress?.Report(1d);
                return;
            }
            catch when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(downloadPath);
            }
        }
    }

    private static Version ResolveCurrentVersion()
    {
        var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (TryParseVersion(informationalVersion, out var informational))
        {
            return informational;
        }

        return entryAssembly.GetName().Version ?? new Version(0, 0, 0);
    }

    private static string ResolveCurrentVersionText(Version version)
    {
        var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusSeparatorIndex = informationalVersion.IndexOf('+');
            var cleaned = plusSeparatorIndex >= 0
                ? informationalVersion[..plusSeparatorIndex]
                : informationalVersion;

            cleaned = cleaned.Trim();

            if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
            {
                cleaned = cleaned[1..];
            }

            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return version.ToString();
    }

    private static string ResolveCurrentExecutablePath()
    {
        var processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("DualClip could not determine the current executable path.");
        }

        return processPath;
    }

    private static GitHubReleaseAssetResponse? SelectAsset(IReadOnlyList<GitHubReleaseAssetResponse>? assets)
    {
        if (assets is not { Count: > 0 })
        {
            return null;
        }

        var currentExecutableName = Path.GetFileName(Environment.ProcessPath);

        if (!string.IsNullOrWhiteSpace(currentExecutableName))
        {
            var currentAsset = assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, currentExecutableName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));

            if (currentAsset is not null)
            {
                return currentAsset;
            }
        }

        foreach (var preferredAssetName in PreferredAssetNames)
        {
            var preferredAsset = assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, preferredAssetName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));

            if (preferredAsset is not null)
            {
                return preferredAsset;
            }
        }

        return assets.FirstOrDefault(asset =>
            asset.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        var preReleaseIndex = normalized.IndexOf('-');
        if (preReleaseIndex >= 0)
        {
            normalized = normalized[..preReleaseIndex];
        }

        if (!Version.TryParse(normalized, out var parsedVersion) || parsedVersion is null)
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    private static string NormalizeVersionText(string? tagName, Version version)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return version.ToString();
        }

        var normalized = tagName.Trim();

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? version.ToString()
            : normalized;
    }

    private static string NormalizeReleaseNotes(string? releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
        {
            return string.Empty;
        }

        var normalized = releaseNotes
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        var filteredLines = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !IsFullChangelogLine(line))
            .ToList();

        return string.Join(
            Environment.NewLine,
            filteredLines.Where((line, index) =>
                !string.IsNullOrWhiteSpace(line)
                || (index > 0 && index < filteredLines.Count - 1 && !string.IsNullOrWhiteSpace(filteredLines[index - 1]) && !string.IsNullOrWhiteSpace(filteredLines[index + 1]))))
            .Trim();
    }

    private static bool IsFullChangelogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.StartsWith("**Full Changelog**:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Full Changelog:", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildBackupExecutablePath(string currentExecutablePath)
    {
        var directory = Path.GetDirectoryName(currentExecutablePath) ?? throw new InvalidOperationException("DualClip could not determine its install folder.");
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(currentExecutablePath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}.previous.exe");
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string BuildUpdateScript()
    {
        return """
@echo off
setlocal
set "CURRENT_EXE=%~1"
set "NEW_EXE=%~2"
set "BACKUP_EXE=%~3"
set "PROCESS_ID=%~4"
set "TARGET_DIR=%~dp1"
set "LOG_PATH=%LOCALAPPDATA%\DualClip\update-helper.log"

if "%CURRENT_EXE%"=="" exit /b 1
if "%NEW_EXE%"=="" exit /b 1
if "%PROCESS_ID%"=="" exit /b 1

>>"%LOG_PATH%" echo [%date% %time%] Starting updater for "%CURRENT_EXE%" using "%NEW_EXE%" waiting on PID %PROCESS_ID%

:wait_for_exit
tasklist /FI "PID eq %PROCESS_ID%" 2>nul | find "%PROCESS_ID%" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_for_exit
)

>>"%LOG_PATH%" echo [%date% %time%] PID %PROCESS_ID% exited. Applying update.
copy /Y "%CURRENT_EXE%" "%BACKUP_EXE%" >nul 2>nul
copy /Y "%NEW_EXE%" "%CURRENT_EXE%" >nul
if errorlevel 1 goto failure

set /a START_ATTEMPT=0

:retry_start
set /a START_ATTEMPT+=1
start "" /D "%TARGET_DIR%" "%CURRENT_EXE%"
if not errorlevel 1 goto launch_success
if %START_ATTEMPT% geq 3 goto failure
>>"%LOG_PATH%" echo [%date% %time%] Relaunch attempt %START_ATTEMPT% failed. Retrying.
timeout /t 1 /nobreak >nul
goto retry_start

:launch_success
>>"%LOG_PATH%" echo [%date% %time%] Relaunched "%CURRENT_EXE%" successfully from "%TARGET_DIR%".
exit /b 0

:failure
>>"%LOG_PATH%" echo [%date% %time%] Update apply failed. Attempting backup launch.
if exist "%BACKUP_EXE%" (
    start "" "%BACKUP_EXE%"
    >>"%LOG_PATH%" echo [%date% %time%] Backup launch requested for "%BACKUP_EXE%".
)
exit /b 1
""";
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetResponse> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAssetResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

public sealed record GitHubUpdateRelease(
    Version Version,
    string VersionText,
    string TagName,
    string DisplayName,
    DateTimeOffset? PublishedAtUtc,
    string HtmlUrl,
    string ReleaseNotes,
    string AssetName,
    string AssetDownloadUrl);

public sealed record GitHubUpdateCheckResult(
    bool IsUpdateAvailable,
    GitHubUpdateRelease? Release,
    string StatusMessage);

public sealed record GitHubPreparedUpdate(
    GitHubUpdateRelease Release,
    string StagingDirectory,
    string DownloadedAssetPath);
