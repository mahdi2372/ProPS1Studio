using System.Collections.ObjectModel;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Env;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.App.ViewModels;

/// <summary>Environment overview + recent conversions. Refreshes on every navigation.</summary>
public sealed class DashboardViewModel : ObservableObject, INavigable
{
    private readonly AppServices _services;
    private EnvironmentReport? _lastReport;
    private bool _isProbing;
    private string _probeStatus = string.Empty;

    private string _osSummary = "Probing...";
    private string _machineSummary = string.Empty;
    private string _powerShellSummary = "...";
    private string _ps2exeSummary = "...";
    private string _upxSummary = "...";
    private string _internetSummary = "...";
    private string _diskSummary = "...";
    private string _elevationSummary = "...";
    private bool _ps2exeReady;
    private bool _upxReady;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(
            ProbeAsync,
            onException: ex => _services.Logger.Write(LogLevel.Error, nameof(DashboardViewModel), ex.Message));
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public ObservableCollection<RecentConversion> RecentConversions { get; } = new();

    public EnvironmentReport? LastReport => _lastReport;

    public bool IsProbing
    {
        get => _isProbing;
        private set => SetProperty(ref _isProbing, value);
    }

    public string ProbeStatus
    {
        get => _probeStatus;
        private set => SetProperty(ref _probeStatus, value);
    }

    public string OsSummary { get => _osSummary; private set => SetProperty(ref _osSummary, value); }
    public string MachineSummary { get => _machineSummary; private set => SetProperty(ref _machineSummary, value); }
    public string PowerShellSummary { get => _powerShellSummary; private set => SetProperty(ref _powerShellSummary, value); }
    public string Ps2exeSummary { get => _ps2exeSummary; private set => SetProperty(ref _ps2exeSummary, value); }
    public string UpxSummary { get => _upxSummary; private set => SetProperty(ref _upxSummary, value); }
    public string InternetSummary { get => _internetSummary; private set => SetProperty(ref _internetSummary, value); }
    public string DiskSummary { get => _diskSummary; private set => SetProperty(ref _diskSummary, value); }
    public string ElevationSummary { get => _elevationSummary; private set => SetProperty(ref _elevationSummary, value); }
    public bool Ps2exeReady { get => _ps2exeReady; private set => SetProperty(ref _ps2exeReady, value); }
    public bool UpxReady { get => _upxReady; private set => SetProperty(ref _upxReady, value); }

    public void OnNavigatedTo()
    {
        LoadRecent();
        if (!IsProbing && !RefreshCommand.IsRunning)
        {
            RefreshCommand.Execute(null);
        }
    }

    private void LoadRecent()
    {
        RecentConversions.Clear();
        foreach (var item in _services.SettingsStore.Load().RecentConversions.Take(10))
        {
            RecentConversions.Add(item);
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        IsProbing = true;
        ProbeStatus = "Probing environment...";
        try
        {
            var report = await _services.Environment.ProbeAsync(cancellationToken);
            _lastReport = report;

            OsSummary = string.IsNullOrWhiteSpace(report.OsProduct)
                ? report.OsVersion
                : $"{report.OsProduct} · {report.OsDisplayVersion} · build {report.OsBuild}";
            MachineSummary = $"{report.MachineName} · {(report.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";
            PowerShellSummary = report.PowerShellVersion ?? "Not available";

            Ps2exeReady = report.Ps2exe?.IsAvailable == true;
            Ps2exeSummary = report.Ps2exe is { IsAvailable: true }
                ? $"Installed · v{report.Ps2exe.ModuleVersion}"
                : $"Missing · {report.Ps2exe?.Error ?? "not found"}";

            UpxReady = report.Upx?.Available == true;
            UpxSummary = report.Upx is { Available: true }
                ? $"Installed · {report.Upx.Version}" + (report.Upx.MatchesPinnedHash ? " · hash pinned" : string.Empty)
                : "Not installed · optional";

            InternetSummary = report.InternetReachable switch
            {
                true => $"Online{FormatLatency(report.InternetLatency)}",
                false => "Offline",
                null => "Unknown",
            };

            DiskSummary = report.TotalDiskBytes > 0
                ? $"{FormatBytes(report.FreeDiskBytes)} free of {FormatBytes(report.TotalDiskBytes)}"
                : "Unknown";

            ElevationSummary = report.IsElevated
                ? "Running elevated (Administrator)"
                : "Standard user · asInvoker";

            ProbeStatus = report.Notes.Count > 0
                ? string.Join("  ", report.Notes)
                : $"Probed at {report.GeneratedAt:HH:mm:ss} · everything looks ready";
        }
        catch (OperationCanceledException)
        {
            ProbeStatus = "Probe cancelled.";
        }
        catch (Exception ex)
        {
            ProbeStatus = $"Probe failed: {ex.Message}";
            _services.Logger.Write(LogLevel.Error, nameof(DashboardViewModel), $"Environment probe failed: {ex.Message}");
        }
        finally
        {
            IsProbing = false;
        }
    }

    private static string FormatLatency(TimeSpan? latency) =>
        latency is { } l ? $" · {Math.Max(1, (int)l.TotalMilliseconds)} ms" : string.Empty;

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
