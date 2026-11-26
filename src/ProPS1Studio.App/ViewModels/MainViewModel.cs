using System;
using System.Linq;
using System.Reflection;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.App.ViewModels;

/// <summary>One sidebar navigation entry.</summary>
public sealed record NavItem(string Key, string Label, string Glyph);

/// <summary>Pages that need a refresh hook when they become visible.</summary>
public interface INavigable
{
    void OnNavigatedTo();
}

/// <summary>Shell-level state: current page, navigation, status bar.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private NavItem? _selectedNav;
    private object _currentViewModel;
    private string _currentPageKey = "dashboard";
    private string _statusText = "Ready";
    private string _presetSelection = string.Empty;

    public MainViewModel(AppServices services)
    {
        _services = services;

        Dashboard = new DashboardViewModel(services);
        Convert = new ConvertViewModel(services);
        BatToExe = new BatToExeViewModel(services);
        Setup = new SetupViewModel(services);
        Settings = new SettingsViewModel(services);
        Diagnostics = new DiagnosticsViewModel(services);
        Help = new HelpViewModel(services);
        Diagnostics.EnvironmentProvider = () => Dashboard.LastReport;
        Settings.Saved += () =>
        {
            var fresh = services.SettingsStore.Load();
            Convert.ApplySettings(fresh);
            BatToExe.ApplySettings(fresh);
            ReloadPresets();
        };
        ReloadPresets();

        NavItems = new List<NavItem>
        {
            new("dashboard", "Dashboard", "\uE80F"),
            new("convert", "Convert", "\uE768"),
            new("bat2exe", "BAT → EXE", "\uE756"),
            new("setup", "Setup", "\uE896"),
            new("settings", "Settings", "\uE713"),
            new("diagnostics", "Diagnostics", "\uE9D9"),
            new("help", "Help", "\uE897"),
        };

        _currentViewModel = Dashboard;
        _selectedNav = NavItems[0];
        Dashboard.OnNavigatedTo();
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    public DashboardViewModel Dashboard { get; }
    public ConvertViewModel Convert { get; }
    public BatToExeViewModel BatToExe { get; }
    public SetupViewModel Setup { get; }
    public SettingsViewModel Settings { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public HelpViewModel Help { get; }

    /// <summary>Saved conversion presets for quick application on the Convert/BAT pages.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Presets { get; } = new();

    /// <summary>True when at least one saved preset exists (controls preset bar visibility).</summary>
    public bool HasPresets => Presets.Count > 0;

    public string PresetSelection
    {
        get => _presetSelection;
        set
        {
            if (!SetProperty(ref _presetSelection, value))
            {
                return;
            }

            ApplyPresetCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand ApplyPresetCommand { get; } = new(ApplyPreset, () => !string.IsNullOrWhiteSpace(PresetSelection));

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var preset in _services.SettingsStore.Load().Presets)
        {
            Presets.Add(preset.Name);
        }

        OnPropertyChanged(nameof(HasPresets));
    }

    private void ApplyPreset()
    {
        var preset = _services.SettingsStore.Load()
            .Presets.FirstOrDefault(p => string.Equals(p.Name, PresetSelection, StringComparison.Ordinal));
        if (preset is null)
        {
            StatusText = $"Preset '{PresetSelection}' no longer exists.";
            return;
        }

        switch (_currentPageKey)
        {
            case "convert":
                Convert.ApplyPreset(preset);
                break;
            case "bat2exe":
                BatToExe.ApplyPreset(preset);
                break;
            default:
                StatusText = "Switch to the Convert or BAT → EXE page to apply a preset.";
                return;
        }

        StatusText = $"Applied preset '{preset.Name}'.";
    }

    public string VersionText =>
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0")
        + " · Developed by Mahdi";

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    /// <summary>Key of the currently displayed page (dashboard, convert, ...).</summary>
    public string CurrentPageKey => _currentPageKey;

    public NavItem? SelectedNavItem
    {
        get => _selectedNav;
        set
        {
            if (!SetProperty(ref _selectedNav, value) || value is null)
            {
                return;
            }

            Navigate(value.Key);
        }
    }

    public void Navigate(string key)
    {
        object page = key switch
        {
            "convert" => Convert,
            "bat2exe" => BatToExe,
            "setup" => Setup,
            "settings" => Settings,
            "diagnostics" => Diagnostics,
            "help" => Help,
            _ => Dashboard,
        };

        CurrentViewModel = page;
        _currentPageKey = key;
        if (page is INavigable navigable)
        {
            navigable.OnNavigatedTo();
        }

        StatusText = NavItems.FirstOrDefault(n => n.Key == key)?.Label ?? "Ready";

        var nav = NavItems.FirstOrDefault(n => n.Key == key);
        if (nav is not null && !ReferenceEquals(_selectedNav, nav))
        {
            _selectedNav = nav;
            OnPropertyChanged(nameof(SelectedNavItem));
        }
    }
}
