using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.App.ViewModels;

/// <summary>The conversion workbench: pick a script, choose options, run the pipeline, show results.</summary>
public sealed class ConvertViewModel : ObservableObject
{
    private readonly AppServices _services;
    private AppSettings _settings;

    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private bool _requireAdministrator;
    private bool _noConsole;
    private bool _compressWithUpx;
    private bool _enableSecretScan = true;
    private bool _encryptScript;
    private bool _protectOutput;
    private bool _overwriteOutput;
    private bool _openFolderAfter = true;
    private bool _signAfterConversion;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _company = string.Empty;
    private string _product = string.Empty;
    private string _copyright = string.Empty;
    private string _fileVersion = string.Empty;
    private string _iconPath = string.Empty;

    private bool _isBusy;
    private string _progressText = string.Empty;
    private bool _hasResult;
    private bool _lastSuccess;
    private string _resultSummary = string.Empty;
    private string _resultDetails = string.Empty;
    private string _compilerOutputText = string.Empty;
    private string? _lastOutputPath;

    public ConvertViewModel(AppServices services)
    {
        _services = services;
        _settings = services.SettingsStore.Load();

        BrowseInputCommand = new RelayCommand(BrowseInput);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        BrowseIconCommand = new RelayCommand(BrowseIcon);
        DeriveOutputCommand = new RelayCommand(DeriveOutput);
        ClearCommand = new RelayCommand(ClearAll);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => _lastOutputPath is not null);
        ConvertCommand = new AsyncRelayCommand(ConvertAsync, onException: ex =>
        {
            _services.Logger.Write(LogLevel.Error, nameof(ConvertViewModel), $"Conversion crashed: {ex}");
            IsBusy = false;
            ProgressText = string.Empty;
            ResultSummary = "Unexpected error: " + ex.Message;
            ResultDetails = ex.ToString();
            HasResult = true;
            LastSuccess = false;
        });
        CancelCommand = new RelayCommand(() => ConvertCommand.Cancel(), () => ConvertCommand.IsRunning);

        ApplySettings(_settings);
    }

    public AsyncRelayCommand ConvertCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseInputCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand BrowseIconCommand { get; }
    public RelayCommand DeriveOutputCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenOutputCommand { get; }

    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();

    public string InputPath
    {
        get => _inputPath;
        set
        {
            if (SetProperty(ref _inputPath, value) && string.IsNullOrWhiteSpace(_outputPath))
            {
                DeriveOutput();
            }
        }
    }

    public string OutputPath { get => _outputPath; set => SetProperty(ref _outputPath, value); }
    public bool RequireAdministrator { get => _requireAdministrator; set => SetProperty(ref _requireAdministrator, value); }
    public bool NoConsole { get => _noConsole; set => SetProperty(ref _noConsole, value); }
    public bool CompressWithUpx { get => _compressWithUpx; set => SetProperty(ref _compressWithUpx, value); }
    public bool EnableSecretScan { get => _enableSecretScan; set => SetProperty(ref _enableSecretScan, value); }
    public bool EncryptScript { get => _encryptScript; set => SetProperty(ref _encryptScript, value); }
    public bool ProtectOutput { get => _protectOutput; set => SetProperty(ref _protectOutput, value); }
    public bool OverwriteOutput { get => _overwriteOutput; set => SetProperty(ref _overwriteOutput, value); }
    public bool OpenFolderAfter { get => _openFolderAfter; set => SetProperty(ref _openFolderAfter, value); }
    public bool SignAfterConversion { get => _signAfterConversion; set => SetProperty(ref _signAfterConversion, value); }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Company { get => _company; set => SetProperty(ref _company, value); }
    public string Product { get => _product; set => SetProperty(ref _product, value); }
    public string Copyright { get => _copyright; set => SetProperty(ref _copyright, value); }
    public string FileVersion { get => _fileVersion; set => SetProperty(ref _fileVersion, value); }
    public string IconPath { get => _iconPath; set => SetProperty(ref _iconPath, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }
    public bool LastSuccess { get => _lastSuccess; private set => SetProperty(ref _lastSuccess, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public string ResultDetails { get => _resultDetails; private set => SetProperty(ref _resultDetails, value); }
    public string CompilerOutputText { get => _compilerOutputText; private set => SetProperty(ref _compilerOutputText, value); }

    /// <summary>Called when settings change so the page defaults stay in sync.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        CompressWithUpx = settings.CompressWithUpx;
        EnableSecretScan = settings.EnableSecretScan;
        EncryptScript = settings.EncryptScript;
        ProtectOutput = settings.ProtectOutput;
        OpenFolderAfter = settings.OpenOutputFolderAfterConversion;
        SignAfterConversion = settings.SignAfterConversion;
        OnPropertyChanged(nameof(HasSignConfig));
    }

    public bool HasSignConfig =>
        !string.IsNullOrWhiteSpace(_settings.SignCertificateThumbprint) ||
        !string.IsNullOrWhiteSpace(_settings.SignPfxPath);

    private void BrowseInput()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a PowerShell script",
            Filter = "PowerShell scripts (*.ps1;*.psm1)|*.ps1;*.psm1|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = InitialDirectoryFor(_settings.LastInputDirectory),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        InputPath = dialog.FileName;
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            DeriveOutput();
        }

        RememberDirectory(nameof(AppSettings.LastInputDirectory), Path.GetDirectoryName(dialog.FileName));
    }

    private void BrowseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose output executable",
            Filter = "Windows executable (*.exe)|*.exe|All files (*.*)|*.*",
            DefaultExt = ".exe",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(OutputPath)
                ? Path.GetFileName(Path.ChangeExtension(InputPath, ".exe") ?? "output.exe")
                : Path.GetFileName(OutputPath),
            InitialDirectory = InitialDirectoryFor(
                string.IsNullOrWhiteSpace(OutputPath) ? _settings.LastOutputDirectory : Path.GetDirectoryName(OutputPath)),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        OutputPath = dialog.FileName;
        RememberDirectory(nameof(AppSettings.LastOutputDirectory), Path.GetDirectoryName(dialog.FileName));
    }

    private void BrowseIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an icon for the executable",
            Filter = "Icon files (*.ico)|*.ico|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            IconPath = dialog.FileName;
        }
    }

    private void DeriveOutput()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            return;
        }

        OutputPath = Path.ChangeExtension(InputPath, ".exe") ?? string.Empty;
    }

    private void ClearAll()
    {
        if (IsBusy)
        {
            return;
        }

        InputPath = string.Empty;
        OutputPath = string.Empty;
        RequireAdministrator = false;
        NoConsole = false;
        CompressWithUpx = _settings.CompressWithUpx;
        EnableSecretScan = _settings.EnableSecretScan;
        EncryptScript = _settings.EncryptScript;
        ProtectOutput = _settings.ProtectOutput;
        OverwriteOutput = false;
        Title = string.Empty;
        Description = string.Empty;
        Company = string.Empty;
        Product = string.Empty;
        Copyright = string.Empty;
        FileVersion = string.Empty;
        IconPath = string.Empty;
        Warnings.Clear();
        Findings.Clear();
        CompilerOutputText = string.Empty;
        HasResult = false;
        ProgressText = string.Empty;
        _lastOutputPath = null;
        OpenOutputCommand.RaiseCanExecuteChanged();
    }

    private void OpenOutput()
    {
        if (_lastOutputPath is not null)
        {
            _services.Shell.OpenInExplorer(_lastOutputPath);
        }
    }

    private async Task ConvertAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            ShowFailure("No script selected.", "Pick a .ps1 file to convert.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            DeriveOutput();
        }

        if (File.Exists(OutputPath) && !OverwriteOutput)
        {
            var choice = MessageBox.Show(
                $"'{Path.GetFileName(OutputPath)}' already exists.\n\nOverwrite it?",
                "Output exists",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }

            OverwriteOutput = true;
        }

        IsBusy = true;
        ProgressText = "Validating and compiling...";
        Warnings.Clear();
        Findings.Clear();
        CompilerOutputText = string.Empty;
        HasResult = false;
        try
        {
            var request = BuildRequest();
            var started = DateTime.Now;
            ConversionResult result;
            try
            {
                result = await _services.Engine.ConvertAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ShowFailure("Conversion cancelled.", "The pipeline was cancelled before it finished.");
                return;
            }

            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            foreach (var finding in result.SecretFindings)
            {
                Findings.Add($"Line {finding.LineNumber} · {finding.RuleName} ({finding.Severity}) · {finding.MaskedValue}");
            }

            CompilerOutputText = result.CompilerOutput.Count > 0
                ? string.Join(Environment.NewLine, result.CompilerOutput)
                : string.Empty;

            LastSuccess = result.Success;
            HasResult = true;

            if (result.Success)
            {
                _lastOutputPath = result.OutputPath;
                OpenOutputCommand.RaiseCanExecuteChanged();
                ResultSummary = $"Created {Path.GetFileName(result.OutputPath)} " +
                                $"({DashboardViewModel.FormatBytes(result.OutputSizeBytes)}) " +
                                $"in {result.Duration.TotalSeconds:0.0} s";
                ResultDetails = BuildDetails(result);
                ProgressText = "Conversion finished.";
                AppendRecent(result, success: true);
                _services.Logger.Write(LogLevel.Info, nameof(ConvertViewModel),
                    $"Converted '{Path.GetFileName(request.InputPath)}' -> '{Path.GetFileName(result.OutputPath)}'.");

                if (OpenFolderAfter && result.OutputPath is not null)
                {
                    _services.Shell.OpenInExplorer(result.OutputPath);
                }
            }
            else
            {
                var error = result.Error;
                ResultSummary = error is null ? "Conversion failed." : $"{error.Message}";
                ResultDetails = error is null ? string.Empty : error.Remediation;
                ProgressText = "Conversion failed.";
                AppendRecent(result, success: false);
                _services.Logger.Write(LogLevel.Warn, nameof(ConvertViewModel),
                    $"Conversion failed for '{Path.GetFileName(request.InputPath)}': {error?.Message}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ConversionRequest BuildRequest()
    {
        SignRequest? signRequest = null;
        if (SignAfterConversion && HasSignConfig)
        {
            signRequest = new SignRequest
            {
                TargetPath = OutputPath,
                CertificateThumbprint = NullIfBlank(_settings.SignCertificateThumbprint),
                StoreLocation = _settings.SignCertificateStoreLocation ?? "CurrentUser",
                PfxPath = NullIfBlank(_settings.SignPfxPath),
                PfxPassword = null, // Never persisted, never displayed; store-based signing is the default path.
                TimestampServer = NullIfBlank(_settings.TimestampServer),
                Description = NullIfBlank(Title),
            };
        }

        return new ConversionRequest
        {
            InputPath = InputPath,
            OutputPath = OutputPath,
            OverwriteOutput = OverwriteOutput,
            RequireAdministrator = RequireAdministrator,
            NoConsole = NoConsole,
            Title = NullIfBlank(Title),
            Description = NullIfBlank(Description),
            Company = NullIfBlank(Company),
            Product = NullIfBlank(Product),
            Copyright = NullIfBlank(Copyright),
            FileVersion = NullIfBlank(FileVersion),
            IconPath = NullIfBlank(IconPath),
            Ps2exeModulePath = NullIfBlank(_settings.Ps2exeModulePath),
            CompressWithUpx = CompressWithUpx,
            UpxForce = _settings.UpxForce,
            UpxFailureIsFatal = false,
            EnableSecretScan = EnableSecretScan,
            SecretFindingsAreFatal = false,
            EncryptScript = EncryptScript,
            ProtectOutput = ProtectOutput,
            SignAfterConversion = signRequest is not null,
            SigningFailureIsFatal = false,
            SignRequest = signRequest,
            TimeoutSeconds = _settings.ConversionTimeoutSeconds,
        };
    }

    private string BuildDetails(ConversionResult result)
    {
        var lines = new List<string>
        {
            $"Output: {result.OutputPath}",
            $"Execution level: {result.ExecutionLevel}",
            $"Console: {(NoConsole ? "GUI (no console window)" : "Console")}",
            $"UPX: {(result.UpxCompressed ? $"compressed {DashboardViewModel.FormatBytes(result.SizeBeforeUpx ?? 0)} -> {DashboardViewModel.FormatBytes(result.OutputSizeBytes)}" : "not compressed")}",
            $"Signed: {(result.Signed ? $"{result.SignatureStatus}" : "no")}",
            $"Encrypted source: {(result.Encrypted ? "yes (AES-256, key embedded)" : "no")}",
            $"Protected output: {(result.Protected ? "yes (encrypted stub + anti-debug)" : "no")}",
            $"Secret findings: {result.SecretFindings.Count}",
            $"Duration: {result.Duration.TotalSeconds:0.00} s",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private void AppendRecent(ConversionResult result, bool success)
    {
        try
        {
            var settings = _services.SettingsStore.Load();
            settings.RecentConversions.Insert(0, new RecentConversion
            {
                Timestamp = DateTime.Now,
                InputPath = InputPath,
                OutputPath = result.OutputPath ?? OutputPath,
                Success = success,
                ErrorSummary = success ? null : result.Error?.Message,
                OutputSizeBytes = result.OutputSizeBytes,
                RequireAdministrator = RequireAdministrator,
                CompressedWithUpx = result.UpxCompressed,
            });
            _services.SettingsStore.Save(settings);
            _settings = settings;
        }
        catch (Exception ex)
        {
            _services.Logger.Write(LogLevel.Warn, nameof(ConvertViewModel), $"Could not persist recent conversions: {ex.Message}");
        }
    }

    private void ShowFailure(string summary, string details)
    {
        HasResult = true;
        LastSuccess = false;
        ResultSummary = summary;
        ResultDetails = details;
        ProgressText = summary;
    }

    private void RememberDirectory(string property, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var settings = _services.SettingsStore.Load();
            if (property == nameof(AppSettings.LastInputDirectory))
            {
                settings.LastInputDirectory = directory;
            }
            else
            {
                settings.LastOutputDirectory = directory;
            }

            _services.SettingsStore.Save(settings);
            _settings = settings;
        }
        catch
        {
            // Directory memory is convenience only.
        }
    }

    private static string InitialDirectoryFor(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : string.Empty;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
