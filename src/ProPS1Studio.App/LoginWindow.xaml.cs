using System;
using System.Runtime.InteropServices;
using System.Windows;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.App;

public partial class LoginWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private readonly AppServices _services;
    private readonly bool _isModal;

    /// <param name="services">Composition root.</param>
    /// <param name="isModal">
    /// True when shown with ShowDialog() as the startup gate (authentication sets DialogResult).
    /// False for self-test capture, where DialogResult must never be touched.
    /// </param>
    public LoginWindow(AppServices services, bool isModal = true)
    {
        _services = services;
        _isModal = isModal;
        InitializeComponent();

        DataContext = new ViewModels.LoginViewModel(services);
        ((ViewModels.LoginViewModel)DataContext).Authenticated += OnAuthenticated;
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

    private void OnAuthenticated(object? sender, EventArgs e)
    {
        _services.Logger.Write(LogLevel.Info, nameof(LoginWindow), "Authentication succeeded; opening the studio.");
        if (_isModal)
        {
            DialogResult = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _services.Logger.Write(LogLevel.Info, nameof(LoginWindow), "Login window close requested.");
        Close();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
