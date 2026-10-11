using System.Collections.ObjectModel;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Setup;

namespace ProPS1Studio.App.ViewModels;

/// <summary>Dependency health and one-click installs for ps2exe and UPX.</summary>
public sealed class SetupViewModel : ObservableObject, INavigable
{
    private readonly AppServices _services;
    private bool _isBusy;
    private string _statusText = string.Empty;
    private DependencyStatus? _ps2exeStatus;
    private DependencyStatus? _upxStatus;

    public SetupViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onException: LogError);
        InstallPs2exeCommand = new AsyncRelayCommand(InstallPs2exeAsync, () => !IsBusy, LogError);
        InstallUpxCommand = new AsyncRelayCommand(InstallUpxAsync, () => !IsBusy, LogError);
        OpenToolsCommand = new RelayCommand(OpenTools);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallPs2exeCommand { get; }
    public AsyncRelayCommand InstallUpxCommand { get; }
    public RelayCommand OpenToolsCommand { get; }

    public ObservableCollection<DependencyStatus> Statuses { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                InstallPs2exeCommand.RaiseCanExecuteChanged();
                InstallUpxCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public DependencyStatus? Ps2exeStatus
    {
        get => _ps2exeStatus;
        private set => SetProperty(ref _ps2exeStatus, value);
    }

    public DependencyStatus? UpxStatus
    {
        get => _upxStatus;
        private set => SetProperty(ref _upxStatus, value);
    }

    public void OnNavigatedTo()
    {
        if (!RefreshCommand.IsRunning)
        {
            RefreshCommand.Execute(null);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusText = "Checking installed tools...";
        try
        {
            var statuses = await _services.Dependencies.GetStatusAsync(cancellationToken);
            Statuses.Clear();
            foreach (var status in statuses)
            {
                Statuses.Add(status);
            }

            var ps2exe = statuses.FirstOrDefault(s => s.Name.Contains("ps2exe", StringComparison.OrdinalIgnoreCase));
            var upx = statuses.FirstOrDefault(s => s.Name.Contains("upx", StringComparison.OrdinalIgnoreCase));
            Ps2exeStatus = ps2exe;
            UpxStatus = upx;
            StatusText = ps2exe is { Installed: true }
                ? $"ps2exe {ps2exe.Version} is ready for conversions."
                : "ps2exe is missing — install it to enable conversions.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Check cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallPs2exeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        var progress = new Progress<string>(message => StatusText = message);
        try
        {
            var result = await _services.Dependencies.InstallPs2exeAsync(progress, cancellationToken);
            StatusText = result.Success
                ? $"ps2exe installed. {result.Details}"
                : $"Install failed: {result.Error?.Message} {result.Error?.Remediation}";
            _services.CapabilityProbe.Invalidate();
            await RefreshAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallUpxAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        var progress = new Progress<string>(message => StatusText = message);
        try
        {
            var result = await _services.Dependencies.InstallUpxAsync(progress, cancellationToken);
            StatusText = result.Success
                ? $"UPX installed. {result.Details}"
                : $"Install failed: {result.Error?.Message} {result.Error?.Remediation}";
            await RefreshAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenTools() => _services.Shell.OpenInExplorer(
        ProPS1Studio.Core.Settings.AppPaths.ToolsDirectory);

    private void LogError(Exception ex)
    {
        _services.Logger.Write(LogLevel.Error, nameof(SetupViewModel), ex.Message);
        StatusText = "Error: " + ex.Message;
        IsBusy = false;
    }
}
