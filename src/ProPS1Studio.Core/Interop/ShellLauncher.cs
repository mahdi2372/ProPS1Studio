using System.ComponentModel;
using System.Diagnostics;
using ProPS1Studio.Core.Errors;

namespace ProPS1Studio.Core.Interop;

public sealed record LaunchResult
{
    public required bool Launched { get; init; }
    public bool UacCancelled { get; init; }
    public AppError? Error { get; init; }
}

public interface IShellLauncher
{
    LaunchResult Run(string path, string? arguments = null, string? workingDirectory = null);
    LaunchResult RunElevated(string path, string? arguments = null, string? workingDirectory = null);
    void OpenInExplorer(string path);
    void OpenWithDefaultApplication(string path);
    void OpenUrl(string url);
}

/// <summary>Launch helpers for the shell: run, run-as-administrator (UAC aware), reveal, open.</summary>
public sealed class ShellLauncher : IShellLauncher
{
    public LaunchResult Run(string path, string? arguments = null, string? workingDirectory = null)
        => Start(path, arguments, workingDirectory, elevated: false);

    public LaunchResult RunElevated(string path, string? arguments = null, string? workingDirectory = null)
        => Start(path, arguments, workingDirectory, elevated: true);

    public void OpenInExplorer(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
            return;
        }

        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
    }

    public void OpenWithDefaultApplication(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return; // Never launch arbitrary schemes (file:, javascript:, ...).
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
    }

    private static LaunchResult Start(string path, string? arguments, string? workingDirectory, bool elevated)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new LaunchResult
            {
                Launched = false,
                Error = new AppError(ErrorCode.LaunchFailed, "No path was provided.", "Select a file to run."),
            };
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                startInfo.Arguments = arguments;
            }

            if (elevated)
            {
                startInfo.Verb = "runas";
            }

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return new LaunchResult
                {
                    Launched = false,
                    Error = ErrorCatalog.LaunchFailed("The shell returned no process."),
                };
            }

            process.Dispose();
            return new LaunchResult { Launched = true };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt.
            return new LaunchResult { Launched = false, UacCancelled = true, Error = ErrorCatalog.UacCancelled() };
        }
        catch (Win32Exception ex)
        {
            return new LaunchResult
            {
                Launched = false,
                Error = ErrorCatalog.LaunchFailed($"{ex.Message} (Win32 {ex.NativeErrorCode})"),
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Launched = false,
                Error = ErrorCatalog.LaunchFailed(ex.Message),
            };
        }
    }
}
