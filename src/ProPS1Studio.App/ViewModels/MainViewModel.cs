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
    private string _statusText = "Ready";

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
        };

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
