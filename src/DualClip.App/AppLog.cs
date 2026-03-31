using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DualClip.Infrastructure;

namespace DualClip.App;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static string _sessionCode = GenerateSessionCode();
    private static string _currentLogPath = BuildSessionLogPath(_sessionCode, DateTimeOffset.Now);
    private static bool _sessionStarted;

    public static string LogFilePath => _currentLogPath;

    public static string SessionCode => _sessionCode;

    public static void StartSession()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsRoot);

            lock (Sync)
            {
                _sessionCode = GenerateSessionCode();
                _currentLogPath = BuildSessionLogPath(_sessionCode, DateTimeOffset.Now);
                File.WriteAllText(_currentLogPath, BuildSessionHeader(), global::System.Text.Encoding.UTF8);
                _sessionStarted = true;
            }
        }
        catch
        {
        }
    }

    public static void Info(string scope, string message, params (string Key, object? Value)[] details)
    {
        Write("INFO", scope, message, details: details);
    }

    public static void Warn(string scope, string message, params (string Key, object? Value)[] details)
    {
        Write("WARN", scope, message, details: details);
    }

    public static void Error(string scope, string message, Exception? exception = null, params (string Key, object? Value)[] details)
    {
        Write("ERROR", scope, message, exception, details);
    }

    public static string ReadCurrentLog()
    {
        try
        {
            EnsureSessionStarted();
            lock (Sync)
            {
                return File.Exists(_currentLogPath)
                    ? File.ReadAllText(_currentLogPath, global::System.Text.Encoding.UTF8)
                    : "No log file has been created yet.";
            }
        }
        catch (Exception ex)
        {
            return $"DualClip could not read the current log.{Environment.NewLine}{ex}";
        }
    }

    public static async Task ExportCurrentLogAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        EnsureSessionStarted();
        var logText = ReadCurrentLog();
        var directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(destinationPath, logText, global::System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static void Write(
        string level,
        string scope,
        string message,
        Exception? exception = null,
        params (string Key, object? Value)[] details)
    {
        try
        {
            EnsureSessionStarted();

            var builder = new StringBuilder();
            builder.Append('[')
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append("] level=")
                .Append(level)
                .Append(" scope=")
                .Append(scope)
                .AppendLine();
            builder.Append("message: ").AppendLine(message);

            foreach (var (key, value) in details)
            {
                builder.Append("detail.")
                    .Append(key)
                    .Append(": ")
                    .AppendLine(FormatValue(value));
            }

            if (exception is not null)
            {
                builder.Append("exception.type: ").AppendLine(exception.GetType().FullName ?? exception.GetType().Name);
                builder.Append("exception.message: ").AppendLine(exception.Message);
                builder.Append("exception.stack:").AppendLine();
                builder.AppendLine(exception.ToString());
            }

            builder.AppendLine("---");

            lock (Sync)
            {
                File.AppendAllText(_currentLogPath, builder.ToString(), global::System.Text.Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static void EnsureSessionStarted()
    {
        if (_sessionStarted)
        {
            return;
        }

        StartSession();
    }

    private static string BuildSessionHeader()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var version = entryAssembly?.GetName().Version?.ToString() ?? "unknown";

        return string.Join(
            Environment.NewLine,
            "=== DualClip Session Log ===",
            $"created_local: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}",
            $"machine_name: {Environment.MachineName}",
            $"os_version: {Environment.OSVersion}",
            $"process_architecture: {RuntimeInformation.ProcessArchitecture}",
            $"process_id: {Environment.ProcessId}",
            $"dotnet_version: {Environment.Version}",
            $"app_version: {version}",
            $"is_packaged: {AppRuntimeInfo.IsPackaged}",
            $"base_directory: {AppContext.BaseDirectory}",
            $"current_directory: {Environment.CurrentDirectory}",
            $"logs_root: {AppPaths.LogsRoot}",
            $"session_code: {_sessionCode}",
            $"log_file_name: {Path.GetFileName(_currentLogPath)}",
            string.Empty,
            "Log format:",
            "- Each entry includes timestamp, level, scope, message, and structured detail.* lines.",
            "- Use this log as the main debugging artifact for runtime, capture, editor, and packaging issues.",
            "---",
            string.Empty);
    }

    private static string BuildSessionLogPath(string sessionCode, DateTimeOffset createdAt)
    {
        return Path.Combine(
            AppPaths.LogsRoot,
            $"DualClip-log-{createdAt:yyyyMMdd_HHmmss}-{sessionCode}.txt");
    }

    private static string GenerateSessionCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);

        var buffer = new char[6];

        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = alphabet[bytes[index] % alphabet.Length];
        }

        return new string(buffer);
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            TimeSpan span => span.ToString(),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
