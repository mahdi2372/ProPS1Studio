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
    }

    public RelayCommand ExportCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenLogFileCommand { get; }
    public RelayCommand OpenDiagnosticsFolderCommand { get; }
    public RelayCommand OpenLastExportCommand { get; }

    /// <summary>Supplied by MainViewModel so the export can include the last environment probe.</summary>
    public Func<EnvironmentReport?>? EnvironmentProvider { get; set; }

    public ObservableCollection<LogEntry> Entries { get; } = new();

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
        });
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
