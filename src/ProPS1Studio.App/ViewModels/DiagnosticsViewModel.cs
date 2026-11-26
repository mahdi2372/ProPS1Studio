using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Env;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.App.ViewModels;

/// <summary>Live log tail plus redacted diagnostics export.</summary>
public sealed class DiagnosticsViewModel : ObservableObject, INavigable
{
    private const int MaxEntries = 500;

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private bool _autoScroll = true;
    private string _statusText = "Live log from this session (secrets are redacted at write time).";
    private string _filterText = string.Empty;
    private string _levelFilter = "All";
    private string? _lastExportPath;

    public DiagnosticsViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var entry in services.Logger.Snapshot)
        {
            Entries.Add(entry);
        }

        services.Logger.EntryLogged += OnEntryLogged;

        ExportCommand = new RelayCommand(Export);
        ClearCommand = new RelayCommand(() => Entries.Clear());
        OpenLogFileCommand = new RelayCommand(OpenLogFile);
        OpenDiagnosticsFolderCommand = new RelayCommand(OpenDiagnosticsFolder);
        OpenLastExportCommand = new RelayCommand(OpenLastExport, () => _lastExportPath is not null);
        ExportLogCommand = new RelayCommand(ExportLog);
        CopyReportCommand = new RelayCommand(CopyReport);
        ApplyFilter();
    }

    public RelayCommand ExportCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenLogFileCommand { get; }
    public RelayCommand OpenDiagnosticsFolderCommand { get; }
    public RelayCommand OpenLastExportCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    public RelayCommand CopyReportCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<string> LevelOptions { get; } = new()
    {
        "All", "Debug", "Info", "Warn", "Error",
    };

    /// <summary>Supplied by MainViewModel so the export can include the last environment probe.</summary>
    public Func<EnvironmentReport?>? EnvironmentProvider { get; set; }

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Entries after applying the level filter and search text.</summary>
    public ObservableCollection<LogEntry> FilteredEntries { get; } = new();

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ApplyFilter();
            }
        }
    }

    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (SetProperty(ref _levelFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void OnNavigatedTo()
    {
        // Nothing heavy; entries stream in via the logger event.
    }

    private void OnEntryLogged(object? sender, LogEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }

            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        var level = _levelFilter;
        var search = _filterText.Trim();

        foreach (var entry in Entries)
        {
            if (!string.Equals(level, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entry.Level.ToString(), level, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (search.Length > 0 &&
                !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredEntries.Add(entry);
        }
    }

    private void ExportLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export log",
            Filter = "Text log (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"ProPS1Studio-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var lines = Entries.Select(e =>
                $"{e.Timestamp:O}\t{e.Level}\t{e.Source}\t{e.Message}");
            File.WriteAllLines(dialog.FileName, lines);
            StatusText = $"Log exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = "Log export failed: " + ex.Message;
        }
    }

    private void CopyReport()
    {
        var report = EnvironmentProvider?.Invoke();
        if (report is null)
        {
            StatusText = "No environment report available yet.";
            return;
        }

        var lines = new[]
        {
            $"Generated: {report.GeneratedAt:O}",
            $"OS: {report.OsDisplayVersion} ({report.OsProduct}, build {report.OsBuild})",
            $"Machine: {report.MachineName}",
            $"Elevated: {report.IsElevated}",
            $".NET: {report.DotNetRuntime}",
            $"PowerShell: {report.PowerShellVersion ?? "not detected"}",
            $"ps2exe: {report.Ps2exe is { IsAvailable: true } ps ? $"installed {ps.ModuleVersion} at {ps.ModulePath}" : $"NOT installed ({report.Ps2exe?.Error})"}",
            $"UPX: {report.Upx is { Available: true } upx ? $"installed at {upx.Path}" : $"NOT installed ({report.Upx?.Error})"}",
        };
        foreach (var note in report.Notes)
        {
            lines = lines.Append($"Note: {note}");
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            StatusText = "Environment report copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = "Copy failed: " + ex.Message;
        }
    }

    private void Export()
    {
        try
        {
            var report = EnvironmentProvider?.Invoke();
            var zipPath = _services.Diagnostics.Export(report);
            _lastExportPath = zipPath;
            OpenLastExportCommand.RaiseCanExecuteChanged();
            StatusText = $"Diagnostics exported to {zipPath}";
        }
        catch (Exception ex)
        {
            StatusText = "Export failed: " + ex.Message;
            _services.Logger.Write(LogLevel.Error, nameof(DiagnosticsViewModel), $"Export failed: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        var path = _services.Logger.LogFilePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _services.Shell.OpenWithDefaultApplication(path);
        }
        else
        {
            StatusText = "No log file has been written yet.";
        }
    }

    private void OpenDiagnosticsFolder()
    {
        Directory.CreateDirectory(Core.Settings.AppPaths.DiagnosticsDirectory);
        _services.Shell.OpenInExplorer(Core.Settings.AppPaths.DiagnosticsDirectory);
    }

    private void OpenLastExport()
    {
        if (_lastExportPath is not null)
        {
            _services.Shell.OpenInExplorer(_lastExportPath);
        }
    }
}
