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
    private string _timestampServer = "https://timestamp.digicert.com";
    private string _signCertificateThumbprint = string.Empty;
    private string _signCertificateStoreLocation = "CurrentUser";
    private string _signPfxPath = string.Empty;
    private bool _signAfterConversion;
    private string _logLevel = "Info";
    private bool _openOutputFolderAfterConversion = true;
    private string _outputPathTemplate = "{name}.exe";
    private string _theme = "dark";
    private string _settingsFilePath;
    private string _logFilePath;
    private string _statusText = string.Empty;

    // Presets
    private readonly List<ConversionPreset> _presets = new();
    private string _presetName = string.Empty;
    private string _presetSelection = string.Empty;
    private bool _presetRequireAdmin;
    private bool _presetNoConsole;
    private bool _presetCompressWithUpx;
    private bool _presetEncrypt = true;
    private bool _presetProtectOutput = true;
    private bool _presetSignAfterConversion;
    private int _presetTimeoutSeconds = 180;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _settingsFilePath = services.SettingsStore.FilePath;
        _logFilePath = services.Logger.LogFilePath;

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        BrowseOutputDirCommand = new RelayCommand(BrowseOutputDir);
        AddPresetCommand = new RelayCommand(AddPreset, () => !string.IsNullOrWhiteSpace(PresetName));
        RemovePresetCommand = new RelayCommand(RemovePreset, () => !string.IsNullOrWhiteSpace(PresetSelection));
        ApplyPresetCommand = new RelayCommand(ApplyPreset, () => !string.IsNullOrWhiteSpace(PresetSelection));
        ExportSettingsCommand = new RelayCommand(ExportSettings);
        ImportSettingsCommand = new RelayCommand(ImportSettings);
        BrowsePs2exeCommand = new RelayCommand(() => BrowseFile(ref _ps2exeModulePath, nameof(Ps2exeModulePath),
            "ps2exe module manifest (*.psd1;*.psm1)|*.psd1;*.psm1|All files (*.*)|*.*"));
        BrowseUpxCommand = new RelayCommand(() => BrowseFile(ref _upxPath, nameof(UpxPath),
            "UPX executable (upx.exe)|upx.exe|All files (*.*)|*.*"));
        BrowsePfxCommand = new RelayCommand(() => BrowseFile(ref _signPfxPath, nameof(SignPfxPath),
            "Certificate files (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*"));
        BrowseCertificateCommand = new RelayCommand(BrowseCertificate);

        LoadFromStore();
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand BrowseOutputDirCommand { get; }
    public RelayCommand BrowsePs2exeCommand { get; }
    public RelayCommand BrowseUpxCommand { get; }
    public RelayCommand BrowsePfxCommand { get; }
    public RelayCommand BrowseCertificateCommand { get; }
    public RelayCommand AddPresetCommand { get; }
    public RelayCommand RemovePresetCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand ExportSettingsCommand { get; }
    public RelayCommand ImportSettingsCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<string> ThemeOptions { get; } = new()
    {
        "dark", "light",
    };

    public System.Collections.ObjectModel.ObservableCollection<string> Presets { get; } = new();

    public string PresetName
    {
        get => _presetName;
        set
        {
            SetProperty(ref _presetName, value);
            AddPresetCommand.RaiseCanExecuteChanged();
        }
    }

    public string PresetSelection
    {
        get => _presetSelection;
        set
        {
            SetProperty(ref _presetSelection, value);
            RemovePresetCommand.RaiseCanExecuteChanged();
            ApplyPresetCommand.RaiseCanExecuteChanged();
        }
    }

    public bool PresetRequireAdmin { get => _presetRequireAdmin; set => SetProperty(ref _presetRequireAdmin, value); }
    public bool PresetNoConsole { get => _presetNoConsole; set => SetProperty(ref _presetNoConsole, value); }
    public bool PresetCompressWithUpx { get => _presetCompressWithUpx; set => SetProperty(ref _presetCompressWithUpx, value); }
    public bool PresetEncrypt { get => _presetEncrypt; set => SetProperty(ref _presetEncrypt, value); }
    public bool PresetProtectOutput { get => _presetProtectOutput; set => SetProperty(ref _presetProtectOutput, value); }
    public bool PresetSignAfterConversion { get => _presetSignAfterConversion; set => SetProperty(ref _presetSignAfterConversion, value); }
    public int PresetTimeoutSeconds { get => _presetTimeoutSeconds; set => SetProperty(ref _presetTimeoutSeconds, value); }

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
    public string OutputPathTemplate
    {
        get => _outputPathTemplate;
        set => SetProperty(ref _outputPathTemplate, string.IsNullOrWhiteSpace(value) ? "{name}.exe" : value.Trim());
    }

    public string Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, string.IsNullOrWhiteSpace(value) ? "dark" : value.Trim().ToLowerInvariant()))
            {
                return;
            }

            // Apply immediately so the user sees the change without saving. StaticResource
            // resolves at load time, so the current view is re-instantiated via a navigation.
            if (App.Current is { MainWindow: System.Windows.Window window } &&
                window.DataContext is MainViewModel main &&
                !string.IsNullOrWhiteSpace(main.StatusText))
            {
                App.Current.SwitchTheme(_theme);
                main.Navigate(main.CurrentPageKey);
            }
        }
    }

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
        OutputPathTemplate = settings.OutputPathTemplate;
        Theme = settings.Theme;

        Presets.Clear();
        _presets.Clear();
        foreach (var preset in settings.Presets)
        {
            _presets.Add(preset);
            Presets.Add(preset.Name);
        }

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
                ? "https://timestamp.digicert.com" : TimestampServer;
            settings.SignCertificateThumbprint = NullIfBlank(SignCertificateThumbprint);
            settings.SignCertificateStoreLocation = NullIfBlank(SignCertificateStoreLocation) ?? "CurrentUser";
            settings.SignPfxPath = NullIfBlank(SignPfxPath);
            settings.SignAfterConversion = SignAfterConversion;
            settings.LogLevel = string.IsNullOrWhiteSpace(LogLevelText) ? "Info" : LogLevelText;
            settings.OpenOutputFolderAfterConversion = OpenOutputFolderAfterConversion;
            settings.OutputPathTemplate = OutputPathTemplate;
            settings.Theme = string.IsNullOrWhiteSpace(Theme) ? "dark" : Theme;

            settings.Presets = _presets
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

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

    /// <summary>Opens the certificate picker and fills in the thumbprint + store location.</summary>
    private void BrowseCertificate()
    {
        var window = new Views.CertificatePickerWindow(SignCertificateStoreLocation);
        var owner = App.Current?.MainWindow;
        if (owner is not null)
        {
            window.Owner = owner;
        }

        if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.SelectedThumbprint))
        {
            SignCertificateThumbprint = window.SelectedThumbprint;
            SignCertificateStoreLocation = string.IsNullOrWhiteSpace(SignCertificateStoreLocation)
                ? "CurrentUser"
                : SignCertificateStoreLocation;
            StatusText = $"Certificate selected: {SignCertificateThumbprint}";
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

    private void AddPreset()
    {
        var name = PresetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        _presets.Add(new ConversionPreset
        {
            Name = name,
            RequireAdmin = PresetRequireAdmin,
            NoConsole = PresetNoConsole,
            CompressWithUpx = PresetCompressWithUpx,
            Encrypt = PresetEncrypt,
            ProtectOutput = PresetProtectOutput,
            SignAfterConversion = PresetSignAfterConversion,
            TimeoutSeconds = PresetTimeoutSeconds,
        });

        Presets.Clear();
        foreach (var preset in _presets)
        {
            Presets.Add(preset.Name);
        }

        PresetSelection = name;
        PresetName = string.Empty;
        StatusText = $"Preset '{name}' saved (apply it on the next save).";
    }

    private void RemovePreset()
    {
        var name = PresetSelection.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        Presets.Remove(name);
        PresetSelection = string.Empty;
        StatusText = $"Preset '{name}' removed (apply it on the next save).";
    }

    /// <summary>Loads the selected preset's values into the preset form for editing.</summary>
    private void ApplyPreset()
    {
        var preset = _presets.FirstOrDefault(p => string.Equals(p.Name, PresetSelection, StringComparison.Ordinal));
        if (preset is null)
        {
            return;
        }

        PresetName = preset.Name;
        PresetRequireAdmin = preset.RequireAdmin;
        PresetNoConsole = preset.NoConsole;
        PresetCompressWithUpx = preset.CompressWithUpx;
        PresetEncrypt = preset.Encrypt;
        PresetProtectOutput = preset.ProtectOutput;
        PresetSignAfterConversion = preset.SignAfterConversion;
        PresetTimeoutSeconds = preset.TimeoutSeconds;
        StatusText = $"Preset '{preset.Name}' loaded into the editor.";
    }

    private void ExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export settings",
            Filter = "JSON settings (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"ProPS1Studio-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.Copy(_settingsFilePath, dialog.FileName, overwrite: true);
                StatusText = $"Settings exported to {dialog.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = "Export failed: " + ex.Message;
            }
        }
    }

    private void ImportSettings()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import settings",
            Filter = "JSON settings (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var choice = MessageBox.Show(
            $"Replace the current settings with '{Path.GetFileName(dialog.FileName)}'?\n\nCurrent settings will be overwritten.",
            "Import settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            File.Copy(dialog.FileName, _settingsFilePath, overwrite: true);
            LoadFromStore();
            StatusText = $"Settings imported from {dialog.FileName}";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = "Import failed: " + ex.Message;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
