using ProPS1Studio.Core.Auth;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Diagnostics;
using ProPS1Studio.Core.Env;
using ProPS1Studio.Core.Interop;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Setup;
using ProPS1Studio.Core.Upx;

namespace ProPS1Studio.App.Services;

/// <summary>
/// Composition root for the app. Builds the real Core service graph once,
/// shares it across all view models, and disposes it on exit.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices()
    {
        Logger = new AppLogger(AppPaths.LogsDirectory);
        SettingsStore = new SettingsStore(AppPaths.SettingsFilePath, Logger);
        KeyAuthConfig = KeyAuthConfigLoader.Load(Logger);
        KeyAuth = new KeyAuthClient(KeyAuthConfig, Logger);
        Runner = new ProcessRunner();
        CapabilityProbe = new Ps2exeCapabilityProbe(Runner, Logger);
        Upx = new UpxManager(Runner, Logger);
        Scanner = new SecretScanner();
        Signer = new AuthenticodeSigner(Runner, Logger);
        Engine = new ConversionEngine(Runner, Logger, CapabilityProbe, Upx, Signer, Scanner);
        BatToExe = new BatToExeEngine(Runner, Logger, Signer, Scanner);
        Environment = new EnvironmentProbe(Runner, CapabilityProbe, Upx);
        Dependencies = new DependencyManager(
            CapabilityProbe,
            new Ps2exeInstaller(Runner, Logger, CapabilityProbe),
            new UpxInstaller(Logger, Upx),
            Upx);
        Diagnostics = new DiagnosticExporter(Logger, SettingsStore);
        Shell = new ShellLauncher();

        Logger.Write(LogLevel.Info, nameof(AppServices), "ProPS1 Studio started.");
    }

    public AppLogger Logger { get; }
    public SettingsStore SettingsStore { get; }
    public KeyAuthConfig KeyAuthConfig { get; }
    public KeyAuthClient KeyAuth { get; }
    public ProcessRunner Runner { get; }
    public Ps2exeCapabilityProbe CapabilityProbe { get; }
    public UpxManager Upx { get; }
    public SecretScanner Scanner { get; }
    public AuthenticodeSigner Signer { get; }
    public ConversionEngine Engine { get; }
    public BatToExeEngine BatToExe { get; }
    public EnvironmentProbe Environment { get; }
    public DependencyManager Dependencies { get; }
    public DiagnosticExporter Diagnostics { get; }
    public ShellLauncher Shell { get; }

    public void Dispose()
    {
        Logger.Write(LogLevel.Info, nameof(AppServices), "ProPS1 Studio shutting down.");
        try
        {
            KeyAuth.Dispose();
        }
        catch
        {
            // Never block shutdown on the HTTP client.
        }

        Logger.Flush();
        Logger.Dispose();
    }
}
