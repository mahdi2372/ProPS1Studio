using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.App.ViewModels;

/// <summary>The BAT → EXE workbench: pick a batch file, choose options, run the pipeline, show results.</summary>
public sealed class BatToExeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private AppSettings _settings;

    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private bool _levelAsInvoker = true;
    private bool _levelRequireAdmin;
    private bool _levelHighestAvailable;
    private bool _noConsole;
    private bool _captureOutputLog;
    private bool _keepConsoleOpen;
    private bool _forwardArguments = true;
    private bool _runFromTempDirectory;
    private bool _keepTempFile;
    private bool _compressPayload = true;
    private bool _encryptPayload = true;
    private bool _protectOutput = true;
    private string _passphrase = string.Empty;
    private bool _enableSecretScan = true;
    private bool _overwriteOutput;
    private bool _openFolderAfter = true;
    private bool _signAfterConversion;
    private string _consoleTitle = string.Empty;
    private string _consoleColorHex = string.Empty;
    private string _extraFilesText = string.Empty;
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

    public BatToExeViewModel(AppServices services)
    {
        _services = services;
        _settings = services.SettingsStore.Load();

        BrowseInputCommand = new RelayCommand(BrowseInput);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        BrowseIconCommand = new RelayCommand(BrowseIcon);
        DeriveOutputCommand = new RelayCommand(DeriveOutput);
        AddExtraFilesCommand = new RelayCommand(AddExtraFiles);
        ClearCommand = new RelayCommand(ClearAll);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => _lastOutputPath is not null);
        ConvertCommand = new AsyncRelayCommand(ConvertAsync, onException: ex =>
        {
            _services.Logger.Write(LogLevel.Error, nameof(BatToExeViewModel), $"Conversion crashed: {ex}");
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
    public RelayCommand AddExtraFilesCommand { get; }
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

    public bool LevelAsInvoker
    {
        get => _levelAsInvoker;
        set
        {
            if (SetProperty(ref _levelAsInvoker, value) && value)
            {
                UnsetLevels(nameof(LevelAsInvoker));
            }
        }
    }

    public bool LevelRequireAdmin
    {
        get => _levelRequireAdmin;
        set
        {
            if (SetProperty(ref _levelRequireAdmin, value) && value)
            {
                UnsetLevels(nameof(LevelRequireAdmin));
            }
        }
    }

    public bool LevelHighestAvailable
    {
        get => _levelHighestAvailable;
        set
        {
            if (SetProperty(ref _levelHighestAvailable, value) && value)
            {
                UnsetLevels(nameof(LevelHighestAvailable));
            }
        }
    }

    public bool NoConsole { get => _noConsole; set => SetProperty(ref _noConsole, value); }
    public bool CaptureOutputLog { get => _captureOutputLog; set => SetProperty(ref _captureOutputLog, value); }
    public bool KeepConsoleOpen { get => _keepConsoleOpen; set => SetProperty(ref _keepConsoleOpen, value); }
    public bool ForwardArguments { get => _forwardArguments; set => SetProperty(ref _forwardArguments, value); }
    public bool RunFromTempDirectory { get => _runFromTempDirectory; set => SetProperty(ref _runFromTempDirectory, value); }
    public bool KeepTempFile { get => _keepTempFile; set => SetProperty(ref _keepTempFile, value); }
    public bool CompressPayload { get => _compressPayload; set => SetProperty(ref _compressPayload, value); }
    public bool EncryptPayload { get => _encryptPayload; set => SetProperty(ref _encryptPayload, value); }
    public bool ProtectOutput { get => _protectOutput; set => SetProperty(ref _protectOutput, value); }
    public string Passphrase { get => _passphrase; set => SetProperty(ref _passphrase, value); }
    public bool EnableSecretScan { get => _enableSecretScan; set => SetProperty(ref _enableSecretScan, value); }
    public bool OverwriteOutput { get => _overwriteOutput; set => SetProperty(ref _overwriteOutput, value); }
    public bool OpenFolderAfter { get => _openFolderAfter; set => SetProperty(ref _openFolderAfter, value); }
    public bool SignAfterConversion { get => _signAfterConversion; set => SetProperty(ref _signAfterConversion, value); }
    public string ConsoleTitle { get => _consoleTitle; set => SetProperty(ref _consoleTitle, value); }
    public string ConsoleColorHex { get => _consoleColorHex; set => SetProperty(ref _consoleColorHex, value); }
    public string ExtraFilesText { get => _extraFilesText; set => SetProperty(ref _extraFilesText, value); }
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
        EnableSecretScan = settings.EnableSecretScan;
        ProtectOutput = settings.ProtectOutput;
        OpenFolderAfter = settings.OpenOutputFolderAfterConversion;
        SignAfterConversion = settings.SignAfterConversion;
        OnPropertyChanged(nameof(HasSignConfig));
    }

    public bool HasSignConfig =>
        !string.IsNullOrWhiteSpace(_settings.SignCertificateThumbprint) ||
        !string.IsNullOrWhiteSpace(_settings.SignPfxPath);

    private void UnsetLevels(string selected)
    {
        if (selected != nameof(LevelAsInvoker) && _levelAsInvoker)
        {
            _levelAsInvoker = false;
            OnPropertyChanged(nameof(LevelAsInvoker));
        }

        if (selected != nameof(LevelRequireAdmin) && _levelRequireAdmin)
        {
            _levelRequireAdmin = false;
            OnPropertyChanged(nameof(LevelRequireAdmin));
        }

        if (selected != nameof(LevelHighestAvailable) && _levelHighestAvailable)
        {
            _levelHighestAvailable = false;
            OnPropertyChanged(nameof(LevelHighestAvailable));
        }
    }

    private void BrowseInput()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a batch script",
            Filter = "Batch scripts (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*",
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

    private void AddExtraFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select files to bundle into the executable",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var existing = new HashSet<string>(
            ParseExtraFiles(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in dialog.FileNames)
        {
            if (existing.Add(file))
            {
                ExtraFilesText = string.IsNullOrWhiteSpace(ExtraFilesText)
                    ? file
                    : ExtraFilesText.TrimEnd('\r', '\n') + Environment.NewLine + file;
            }
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
        LevelAsInvoker = true;
        LevelRequireAdmin = false;
        LevelHighestAvailable = false;
        NoConsole = false;
        CaptureOutputLog = false;
        KeepConsoleOpen = false;
        ForwardArguments = true;
        RunFromTempDirectory = false;
        KeepTempFile = false;
        CompressPayload = true;
        EncryptPayload = true;
        ProtectOutput = _settings.ProtectOutput;
        Passphrase = string.Empty;
        EnableSecretScan = _settings.EnableSecretScan;
        OverwriteOutput = false;
        ConsoleTitle = string.Empty;
        ConsoleColorHex = string.Empty;
        ExtraFilesText = string.Empty;
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
            ShowFailure("No batch file selected.", "Pick a .bat or .cmd file to convert.");
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
            BatConversionResult result;
            try
            {
                result = await _services.BatToExe.ConvertAsync(request, cancellationToken);
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
                _services.Logger.Write(LogLevel.Info, nameof(BatToExeViewModel),
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
                _services.Logger.Write(LogLevel.Warn, nameof(BatToExeViewModel),
                    $"Conversion failed for '{Path.GetFileName(request.InputPath)}': {error?.Message}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private BatConversionRequest BuildRequest()
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

        return new BatConversionRequest
        {
            InputPath = InputPath,
            OutputPath = OutputPath,
            OverwriteOutput = OverwriteOutput,
            ExecutionLevel = CurrentExecutionLevel(),
            NoConsole = NoConsole,
            CaptureOutputLog = CaptureOutputLog,
            KeepConsoleOpen = KeepConsoleOpen,
            ForwardArguments = ForwardArguments,
            RunFromTempDirectory = RunFromTempDirectory,
            KeepTempFile = KeepTempFile,
            ConsoleTitle = NullIfBlank(ConsoleTitle),
            ConsoleColorHex = NullIfBlank(ConsoleColorHex),
            CompressPayload = CompressPayload,
            EncryptPayload = EncryptPayload,
            ProtectOutput = ProtectOutput,
            Passphrase = NullIfBlank(Passphrase),
            ExtraFilePaths = ParseExtraFiles(),
            Title = NullIfBlank(Title),
            Description = NullIfBlank(Description),
            Company = NullIfBlank(Company),
            Product = NullIfBlank(Product),
            Copyright = NullIfBlank(Copyright),
            FileVersion = NullIfBlank(FileVersion),
            IconPath = NullIfBlank(IconPath),
            EnableSecretScan = EnableSecretScan,
            SecretFindingsAreFatal = false,
            SignAfterConversion = signRequest is not null,
            SigningFailureIsFatal = false,
            SignRequest = signRequest,
            TimeoutSeconds = _settings.ConversionTimeoutSeconds,
        };
    }

    private string BuildDetails(BatConversionResult result)
    {
        var payload = result.PayloadCompressed ? "GZip" : "raw";
        if (result.PayloadEncrypted)
        {
            payload += " + AES-256";
        }

        payload += result.PayloadObfuscated ? " + XOR (passphrase)" : " + Base64";
        var lines = new List<string>
        {
            $"Output: {result.OutputPath}",
            $"Execution level: {result.ExecutionLevel}",
            $"Console: {(NoConsole ? "GUI (no console window)" : "Console")}" +
                (CaptureOutputLog ? ", output log saved" : string.Empty) +
                (KeepConsoleOpen ? ", pause on exit" : string.Empty),
            $"Payload: {payload}",
            $"Protected output: {(result.Protected ? "yes (encrypted stub + anti-debug)" : "no")}",
            $"Extra files: {result.ExtraFileCount}",
            $"Signed: {(result.Signed ? $"{result.SignatureStatus}" : "no")}",
            $"Secret findings: {result.SecretFindings.Count}",
            $"Duration: {result.Duration.TotalSeconds:0.00} s",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private void AppendRecent(BatConversionResult result, bool success)
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
                RequireAdministrator = CurrentExecutionLevel() == BatExecutionLevel.RequireAdministrator,
                CompressedWithUpx = false,
            });
            _services.SettingsStore.Save(settings);
            _settings = settings;
        }
        catch (Exception ex)
        {
            _services.Logger.Write(LogLevel.Warn, nameof(BatToExeViewModel), $"Could not persist recent conversions: {ex.Message}");
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

    private List<string> ParseExtraFiles() =>
        ExtraFilesText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToList();

    private BatExecutionLevel CurrentExecutionLevel() =>
        _levelRequireAdmin ? BatExecutionLevel.RequireAdministrator
        : _levelHighestAvailable ? BatExecutionLevel.HighestAvailable
        : BatExecutionLevel.AsInvoker;

    private static string InitialDirectoryFor(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : string.Empty;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
