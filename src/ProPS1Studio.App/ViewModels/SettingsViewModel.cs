using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.App.ViewModels;

/// <summary>Persisted preferences. Loads a working copy; Save merges it over the existing store.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    private string _defaultOutputDirectory = string.Empty;
    private string _ps2exeModulePath = string.Empty;
    private string _upxPath = string.Empty;
    private bool _compressWithUpx;
    private bool _upxForce;
    private bool _enableSecretScan = true;
    private bool _encryptScript;
    private bool _protectOutput = true;
    private string _extraSecretPatternsText = string.Empty;
    private int _conversionTimeoutSeconds = 180;
    private string _timestampServer = "http://timestamp.digicert.com";
    private string _signCertificateThumbprint = string.Empty;
    private string _signCertificateStoreLocation = "CurrentUser";
    private string _signPfxPath = string.Empty;
    private bool _signAfterConversion;
    private string _logLevel = "Info";
    private bool _openOutputFolderAfterConversion = true;
    private string _settingsFilePath;
    private string _logFilePath;
    private string _statusText = string.Empty;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _settingsFilePath = services.SettingsStore.FilePath;
        _logFilePath = services.Logger.LogFilePath;

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        BrowseOutputDirCommand = new RelayCommand(BrowseOutputDir);
        BrowsePs2exeCommand = new RelayCommand(() => BrowseFile(ref _ps2exeModulePath, nameof(Ps2exeModulePath),
            "ps2exe module manifest (*.psd1;*.psm1)|*.psd1;*.psm1|All files (*.*)|*.*"));
        BrowseUpxCommand = new RelayCommand(() => BrowseFile(ref _upxPath, nameof(UpxPath),
            "UPX executable (upx.exe)|upx.exe|All files (*.*)|*.*"));
        BrowsePfxCommand = new RelayCommand(() => BrowseFile(ref _signPfxPath, nameof(SignPfxPath),
            "Certificate files (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*"));

        LoadFromStore();
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand BrowseOutputDirCommand { get; }
    public RelayCommand BrowsePs2exeCommand { get; }
    public RelayCommand BrowseUpxCommand { get; }
    public RelayCommand BrowsePfxCommand { get; }

    public event Action? Saved;

    public string SettingsFilePath { get => _settingsFilePath; private set => SetProperty(ref _settingsFilePath, value); }
    public string LogFilePath { get => _logFilePath; private set => SetProperty(ref _logFilePath, value); }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DefaultOutputDirectory { get => _defaultOutputDirectory; set => SetProperty(ref _defaultOutputDirectory, value); }
    public string Ps2exeModulePath { get => _ps2exeModulePath; set => SetProperty(ref _ps2exeModulePath, value); }
    public string UpxPath { get => _upxPath; set => SetProperty(ref _upxPath, value); }
    public bool CompressWithUpx { get => _compressWithUpx; set => SetProperty(ref _compressWithUpx, value); }
    public bool UpxForce { get => _upxForce; set => SetProperty(ref _upxForce, value); }
    public bool EnableSecretScan { get => _enableSecretScan; set => SetProperty(ref _enableSecretScan, value); }
    public bool EncryptScript { get => _encryptScript; set => SetProperty(ref _encryptScript, value); }
    public bool ProtectOutput { get => _protectOutput; set => SetProperty(ref _protectOutput, value); }
    public string ExtraSecretPatternsText { get => _extraSecretPatternsText; set => SetProperty(ref _extraSecretPatternsText, value); }
    public int ConversionTimeoutSeconds { get => _conversionTimeoutSeconds; set => SetProperty(ref _conversionTimeoutSeconds, value); }
    public string TimestampServer { get => _timestampServer; set => SetProperty(ref _timestampServer, value); }
    public string SignCertificateThumbprint { get => _signCertificateThumbprint; set => SetProperty(ref _signCertificateThumbprint, value); }
    public string SignCertificateStoreLocation { get => _signCertificateStoreLocation; set => SetProperty(ref _signCertificateStoreLocation, value); }
    public string SignPfxPath { get => _signPfxPath; set => SetProperty(ref _signPfxPath, value); }
    public bool SignAfterConversion { get => _signAfterConversion; set => SetProperty(ref _signAfterConversion, value); }
    public string LogLevelText { get => _logLevel; set => SetProperty(ref _logLevel, value); }
    public bool OpenOutputFolderAfterConversion { get => _openOutputFolderAfterConversion; set => SetProperty(ref _openOutputFolderAfterConversion, value); }

    public void LoadFromStore()
    {
        var settings = _services.SettingsStore.Load();
        DefaultOutputDirectory = settings.DefaultOutputDirectory ?? string.Empty;
        Ps2exeModulePath = settings.Ps2exeModulePath ?? string.Empty;
        UpxPath = settings.UpxPath ?? string.Empty;
        CompressWithUpx = settings.CompressWithUpx;
        UpxForce = settings.UpxForce;
        EnableSecretScan = settings.EnableSecretScan;
        EncryptScript = settings.EncryptScript;
        ProtectOutput = settings.ProtectOutput;
        ExtraSecretPatternsText = string.Join(Environment.NewLine, settings.ExtraSecretPatterns);
        ConversionTimeoutSeconds = settings.ConversionTimeoutSeconds;
        TimestampServer = settings.TimestampServer;
        SignCertificateThumbprint = settings.SignCertificateThumbprint ?? string.Empty;
        SignCertificateStoreLocation = settings.SignCertificateStoreLocation ?? "CurrentUser";
        SignPfxPath = settings.SignPfxPath ?? string.Empty;
        SignAfterConversion = settings.SignAfterConversion;
        LogLevelText = settings.LogLevel;
        OpenOutputFolderAfterConversion = settings.OpenOutputFolderAfterConversion;
        StatusText = string.Empty;
    }

    private void Save()
    {
        if (ConversionTimeoutSeconds < 15)
        {
            ConversionTimeoutSeconds = 15;
        }

        if (ConversionTimeoutSeconds > 3600)
        {
            ConversionTimeoutSeconds = 3600;
        }

        try
        {
            var settings = _services.SettingsStore.Load();
            settings.DefaultOutputDirectory = NullIfBlank(DefaultOutputDirectory);
            settings.Ps2exeModulePath = NullIfBlank(Ps2exeModulePath);
            settings.UpxPath = NullIfBlank(UpxPath);
            settings.CompressWithUpx = CompressWithUpx;
            settings.UpxForce = UpxForce;
            settings.EnableSecretScan = EnableSecretScan;
            settings.EncryptScript = EncryptScript;
            settings.ProtectOutput = ProtectOutput;
            settings.ExtraSecretPatterns = ExtraSecretPatternsText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            settings.ConversionTimeoutSeconds = ConversionTimeoutSeconds;
            settings.TimestampServer = string.IsNullOrWhiteSpace(TimestampServer)
                ? "http://timestamp.digicert.com" : TimestampServer;
            settings.SignCertificateThumbprint = NullIfBlank(SignCertificateThumbprint);
            settings.SignCertificateStoreLocation = NullIfBlank(SignCertificateStoreLocation) ?? "CurrentUser";
            settings.SignPfxPath = NullIfBlank(SignPfxPath);
            settings.SignAfterConversion = SignAfterConversion;
            settings.LogLevel = string.IsNullOrWhiteSpace(LogLevelText) ? "Info" : LogLevelText;
            settings.OpenOutputFolderAfterConversion = OpenOutputFolderAfterConversion;

            _services.SettingsStore.Save(settings);
            StatusText = $"Saved at {DateTime.Now:HH:mm:ss}.";
            _services.Logger.Write(LogLevel.Info, nameof(SettingsViewModel), "Settings saved.");
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = "Save failed: " + ex.Message;
            _services.Logger.Write(LogLevel.Error, nameof(SettingsViewModel), $"Settings save failed: {ex.Message}");
        }
    }

    private void Reset()
    {
        var choice = MessageBox.Show(
            "Reset all settings to their defaults?\n\nRecent conversions will also be cleared.",
            "Reset settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.SettingsStore.Save(new AppSettings());
            LoadFromStore();
            StatusText = "Settings reset to defaults.";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = "Reset failed: " + ex.Message;
        }
    }

    private void BrowseOutputDir()
    {
        // WPF has no folder picker without a package; use the file dialog's folder trick
        // only if the directory exists, otherwise fall back to a save dialog prompt.
        var dialog = new OpenFileDialog
        {
            Title = "Select any file inside the desired output folder",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "Select this folder",
            InitialDirectory = Directory.Exists(DefaultOutputDirectory) ? DefaultOutputDirectory : string.Empty,
        };
        if (dialog.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                DefaultOutputDirectory = dir;
            }
        }
    }

    private void BrowseFile(ref string field, string propertyName, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select file",
            Filter = filter,
            CheckFileExists = true,
            FileName = Path.GetFileName(field),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(field)) ? Path.GetDirectoryName(field) : string.Empty,
        };
        if (dialog.ShowDialog() == true)
        {
            field = dialog.FileName;
            OnPropertyChanged(propertyName);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
