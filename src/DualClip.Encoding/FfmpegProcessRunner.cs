using System.Diagnostics;
using System.Text;

namespace DualClip.Encoding;

internal sealed class FfmpegProcessRunner
{
    public async Task RunAsync(string ffmpegPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(ffmpegPath, arguments);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start ffmpeg at '{ffmpegPath}'.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}.{Environment.NewLine}{TrimStderr(stderr)}");
        }
    }

    private static Process CreateProcess(string ffmpegPath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
    }

    private static string TrimStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return " ffmpeg did not return any error text.";
        }

        var lines = stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(25);

        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
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
}
