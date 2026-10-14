using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VsConan.Core;

public sealed class ProcessResult
{
    public ProcessResult(int exitCode, string standardOutput)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }
}

internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion. stderr lines, and stdout lines unless
    /// <paramref name="streamStandardOutput"/> is false, are passed to <paramref name="onOutput"/>
    /// (on a background thread). stdout is also returned so callers can parse it.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        bool streamStandardOutput = true)
    {
        var startInfo = new ProcessStartInfo(fileName, string.Join(" ", arguments.Select(QuoteArgument)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        startInfo.EnvironmentVariables["NO_COLOR"] = "1";
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            if (streamStandardOutput) onOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            onOutput?.Invoke(e.Data);
        };
        process.Exited += (_, _) =>
        {
            // The parameterless overload waits for the redirected streams to drain.
            process.WaitForExit();
            exited.TrySetResult(process.ExitCode);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (cancellationToken.Register(() => TryKill(process)))
        {
            var exitCode = await exited.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (stdout) return new ProcessResult(exitCode, stdout.ToString());
        }
    }

    /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            sb.Append(c);
            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
