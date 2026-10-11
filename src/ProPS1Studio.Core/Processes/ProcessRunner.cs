using System.Diagnostics;

namespace ProPS1Studio.Core.Processes;

/// <summary>Result of running an external process to completion.</summary>
public sealed record ProcessResult
{
    public required int ExitCode { get; init; }
    public required TimeSpan Duration { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool FailedToStart { get; init; }
    public string? StartError { get; init; }

    public bool Success => !TimedOut && !FailedToStart && ExitCode == 0;
}

/// <summary>Request describing an external process to launch.</summary>
public sealed class ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
    public int TimeoutMilliseconds { get; init; } = 180_000;
}

/// <summary>Runs external processes with captured output, line events, timeout and tree-kill.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var started = DateTime.UtcNow;

        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in spec.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }
        process.StartInfo = startInfo;

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onErrorLine?.Invoke(e.Data);
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        try
        {
            if (!process.Start())
            {
                return new ProcessResult
                {
                    ExitCode = -1,
                    Duration = DateTime.UtcNow - started,
                    FailedToStart = true,
                    StartError = "Process.Start returned false.",
                };
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult
            {
                ExitCode = -1,
                Duration = DateTime.UtcNow - started,
                FailedToStart = true,
                StartError = ex.Message,
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (spec.TimeoutMilliseconds > 0)
        {
            timeoutCts.CancelAfter(spec.TimeoutMilliseconds);
        }

        var timedOut = false;
        try
        {
            await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            // Give redirected streams a moment to flush final lines.
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }

        // Wait for stream drain.
        try
        {
            process.WaitForExit(5_000);
        }
        catch
        {
            // ignored
        }

        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            exitCode = -1;
        }

        if (cancellationToken.IsCancellationRequested && !timedOut)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new ProcessResult
        {
            ExitCode = exitCode,
            Duration = DateTime.UtcNow - started,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            TimedOut = timedOut,
        };
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
            // Process may have exited between the check and the kill call.
        }
    }
}
