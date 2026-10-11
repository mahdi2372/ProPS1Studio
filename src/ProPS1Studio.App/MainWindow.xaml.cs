using System.Runtime.InteropServices;
using System.Windows;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private readonly AppServices _services;
    private bool _isMaximized;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DataContext = new ViewModels.MainViewModel(services);
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dark = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        _isMaximized = !_isMaximized;
        WindowState = _isMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _services.Logger.Write(LogLevel.Info, nameof(MainWindow), "Window close requested.");
        Close();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        _isMaximized = WindowState == WindowState.Maximized;
        base.OnStateChanged(e);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
