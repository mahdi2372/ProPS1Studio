using System.Net.Http;
using System.Runtime.Versioning;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Upx;

namespace ProPS1Studio.Core.Env;

public sealed record EnvironmentReport
{
    public DateTime GeneratedAt { get; init; }
    public string OsVersion { get; init; } = string.Empty;
    public string OsProduct { get; init; } = string.Empty;
    public string OsDisplayVersion { get; init; } = string.Empty;
    public string OsBuild { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public bool Is64BitOperatingSystem { get; init; }
    public bool IsElevated { get; init; }
    public string DotNetRuntime { get; init; } = string.Empty;
    public string? PowerShellVersion { get; init; }
    public Ps2exeCapability? Ps2exe { get; init; }
    public UpxAvailability? Upx { get; init; }
    public long FreeDiskBytes { get; init; }
    public long TotalDiskBytes { get; init; }
    public bool? InternetReachable { get; init; }
    public TimeSpan? InternetLatency { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public interface IEnvironmentProbe
{
    Task<EnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed class EnvironmentProbe : IEnvironmentProbe
{
    private readonly IProcessRunner _runner;
    private readonly IPs2exeCapabilityProbe _capabilityProbe;
    private readonly IUpxPacker _upx;
    private static readonly HttpClient Http = CreateClient();

    public EnvironmentProbe(IProcessRunner runner, IPs2exeCapabilityProbe capabilityProbe, IUpxPacker upx)
    {
        _runner = runner;
        _capabilityProbe = capabilityProbe;
        _upx = upx;
    }

    public async Task<EnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var (osProduct, osDisplay, osBuild) = ReadWindowsVersionRegistry();
        var psVersion = await ProbePowerShellVersionAsync(cancellationToken).ConfigureAwait(false);
        if (psVersion is null)
        {
            notes.Add("PowerShell could not be launched; conversions will not work until it is available.");
        }

        var ps2exe = await _capabilityProbe.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!ps2exe.IsAvailable)
        {
            notes.Add("ps2exe is not installed; use the Setup page to install it.");
        }

        var upx = await _upx.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!upx.Available)
        {
            notes.Add("UPX is not installed; it is optional and only needed for compression.");
        }
        else if (!upx.MatchesPinnedHash)
        {
            notes.Add($"UPX at '{upx.Path}' does not match the hash-pinned official 5.2.1 build; it will still work but is not the pinned release.");
        }

        var (free, total) = ReadDiskSpace();
        var (reachable, latency) = await ProbeInternetAsync(cancellationToken).ConfigureAwait(false);
        if (reachable == false)
        {
            notes.Add("No internet connectivity detected; tool downloads from the Setup page will fail until it is restored.");
        }

        return new EnvironmentReport
        {
            GeneratedAt = DateTime.Now,
            OsVersion = Environment.OSVersion.VersionString,
            OsProduct = osProduct,
            OsDisplayVersion = osDisplay,
            OsBuild = osBuild,
            MachineName = Environment.MachineName,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            IsElevated = IsElevated(),
            DotNetRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            PowerShellVersion = psVersion,
            Ps2exe = ps2exe,
            Upx = upx,
            FreeDiskBytes = free,
            TotalDiskBytes = total,
            InternetReachable = reachable,
            InternetLatency = latency,
            Notes = notes,
        };
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (string Product, string DisplayVersion, string Build) ReadWindowsVersionRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null)
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            var product = key.GetValue("ProductName") as string ?? string.Empty;
            var display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string ?? string.Empty;
            var build = key.GetValue("CurrentBuildNumber") as string ?? key.GetValue("CurrentBuild") as string ?? string.Empty;
            return (product, display, build);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private async Task<string?> ProbePowerShellVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec
                {
                    FileName = "powershell.exe",
                    Arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()" },
                    TimeoutMilliseconds = 30_000,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var version = result.StdOut.Trim();
            return result.Success && version.Length > 0 ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static (long Free, long Total) ReadDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.RootDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task<(bool? Reachable, TimeSpan? Latency)> ProbeInternetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var started = DateTime.UtcNow;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync("https://api.nuget.org/v3/index.json", cts.Token)
                .ConfigureAwait(false);
            var latency = DateTime.UtcNow - started;
            return (response.IsSuccessStatusCode, latency);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProPS1Studio/1.0");
        return client;
    }
}
