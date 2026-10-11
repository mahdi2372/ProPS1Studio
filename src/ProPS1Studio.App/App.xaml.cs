using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = new AppServices();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var selftest = Environment.GetEnvironmentVariable("PROPS_SELFTEST") == "1";
        var selftestPage = Environment.GetEnvironmentVariable("PROPS_SELFTEST_PAGE");

        // Self-test capture of the login page: show it non-modal and shut down after capture.
        if (selftest && string.Equals(selftestPage, "login", StringComparison.OrdinalIgnoreCase))
        {
            var login = new LoginWindow(_services, isModal: false);
            MainWindow = login;
            login.Show();
            _ = RunSelfTestAsync(login);
            return;
        }

        // Login gate: shown before the main window (skipped for self-test runs and PROPS_AUTH_BYPASS=1).
        if (!selftest && Environment.GetEnvironmentVariable("PROPS_AUTH_BYPASS") != "1")
        {
            if (_services.KeyAuthConfig.IsConfigured)
            {
                // The login window is transient: with ShutdownMode=OnMainWindowClose, closing it
                // (it is Application.MainWindow until the studio opens) would shut the whole app
                // down the moment authentication succeeds. Switch to explicit shutdown for the
                // duration of the gate; the real MainWindow re-enables OnMainWindowClose below.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var login = new LoginWindow(_services);
                if (login.ShowDialog() != true)
                {
                    _services.Logger.Write(LogLevel.Info, nameof(App),
                        "Login gate closed without authenticating; exiting.");
                    Shutdown(0);
                    return;
                }
            }
            else
            {
                _services.Logger.Write(LogLevel.Warn, nameof(App),
                    "Auth gate skipped: KeyAuth is not configured (keyauth.json missing or incomplete).");
            }
        }

        var window = new MainWindow(_services);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        if (selftest)
        {
            _ = RunSelfTestAsync(window);
        }
    }

    private async Task RunSelfTestAsync(Window window)
    {
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            await Task.Delay(1800);

            var page = Environment.GetEnvironmentVariable("PROPS_SELFTEST_PAGE");
            _services?.Logger.Write(LogLevel.Info, nameof(App), $"Self-test requested page: '{page}'");
            if (!string.IsNullOrWhiteSpace(page) && page != "dashboard" &&
                window.DataContext is ViewModels.MainViewModel main)
            {
                main.Navigate(page);
                _services?.Logger.Write(LogLevel.Info, nameof(App),
                    $"Self-test after Navigate: current={main.CurrentViewModel.GetType().Name} status='{main.StatusText}'");
                await Task.Delay(1800);
            }

            var outPath = Environment.GetEnvironmentVariable("PROPS_SELFTEST_OUT");
            if (string.IsNullOrWhiteSpace(outPath))
            {
                outPath = Path.Combine(Path.GetTempPath(), "props1-selftest.png");
            }

            CaptureWindow(window, outPath);
            _services?.Logger.Write(LogLevel.Info, nameof(App),
                $"Self-test capture written: {outPath} (vm={window.DataContext?.GetType().Name}, " +
                $"current={(window.DataContext as ViewModels.MainViewModel)?.CurrentViewModel?.GetType().Name})");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            Shutdown(1);
        }
    }

    private static void CaptureWindow(Window window, string outPath)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var w = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var h = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(outPath);
        encoder.Save(stream);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        _services?.Dispose();
        _services = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);
        e.Handled = true;
        if (Environment.GetEnvironmentVariable("PROPS_SELFTEST") == "1")
        {
            Shutdown(1);
            return;
        }
        MessageBox.Show(
            "An unexpected error occurred:\n\n" + e.Exception.Message,
            "ProPS1 Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogFatal(ex);
        }
    }

    private void LogFatal(Exception ex)
    {
        try
        {
            _services?.Logger.Write(LogLevel.Error, nameof(App), $"Unhandled: {ex}");
        }
        catch
        {
            // Last resort: never let logging crash the crash handler.
        }
    }
}
