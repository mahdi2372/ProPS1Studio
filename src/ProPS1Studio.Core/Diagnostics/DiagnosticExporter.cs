using System.IO.Compression;
using System.Text;
using ProPS1Studio.Core.Env;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.Diagnostics;

public interface IDiagnosticExporter
{
    /// <summary>Writes a redacted diagnostics archive and returns its full path.</summary>
    string Export(EnvironmentReport? environment);
}

/// <summary>
/// Packages environment info, settings, recent conversions and logs into a shareable zip.
/// All log content was sanitized at write time by <see cref="LogSanitizer"/>.
/// </summary>
public sealed class DiagnosticExporter : IDiagnosticExporter
{
    private readonly IAppLogger _logger;
    private readonly SettingsStore _settingsStore;

    public DiagnosticExporter(IAppLogger logger, SettingsStore settingsStore)
    {
        _logger = logger;
        _settingsStore = settingsStore;
    }

    public string Export(EnvironmentReport? environment)
    {
        Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(AppPaths.DiagnosticsDirectory, $"prop-s1-diagnostics-{stamp}.zip");

        var settings = _settingsStore.Load();

        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "readme.txt", BuildReadme());
        WriteEntry(archive, "environment.txt", BuildEnvironment(environment));
        WriteEntry(archive, "settings.json", BuildSettings(settings));
        WriteEntry(archive, "recent-conversions.txt", BuildConversions(settings));
        WriteEntry(archive, "log.txt", BuildLog());
        WriteEntry(archive, "application.txt", BuildApplicationInfo());

        _logger.Write(LogLevel.Info, nameof(DiagnosticExporter), $"Diagnostics exported to '{zipPath}'.");
        return zipPath;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildReadme() => string.Join(Environment.NewLine,
        "ProPS1Studio diagnostics archive",
        $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        "",
        "Contents are sanitized: log lines pass through a secret redaction filter and",
        "no passwords or certificate secrets are included. Review before sharing.",
        "");

    private static string BuildEnvironment(EnvironmentReport? report)
    {
        if (report is null)
        {
            return "Environment report was not captured for this export." + Environment.NewLine;
        }

        var sb = new StringBuilder();
        void Line(string key, object? value) => sb.AppendLine($"{key,-28}: {value}");

        Line("Generated", report.GeneratedAt);
        Line("OS product", report.OsProduct);
        Line("OS version", report.OsVersion);
        Line("OS display", report.OsDisplayVersion);
        Line("OS build", report.OsBuild);
        Line("Machine", report.MachineName);
        Line("64-bit OS", report.Is64BitOperatingSystem);
        Line("Elevated", report.IsElevated);
        Line(".NET", report.DotNetRuntime);
        Line("PowerShell", report.PowerShellVersion ?? "not available");
        Line("ps2exe", report.Ps2exe is { IsAvailable: true }
            ? $"installed, version {report.Ps2exe.ModuleVersion}, {report.Ps2exe.Parameters.Count} parameters"
            : $"not available ({report.Ps2exe?.Error ?? "unknown"})");
        Line("UPX", report.Upx is { Available: true }
            ? $"{report.Upx.Version} at {report.Upx.Path} (pinned hash: {report.Upx.MatchesPinnedHash})"
            : $"not available ({report.Upx?.Error ?? "unknown"})");
        Line("Free disk", ErrorCatalogSafe(report.FreeDiskBytes));
        Line("Total disk", ErrorCatalogSafe(report.TotalDiskBytes));
        Line("Internet", report.InternetReachable is null
            ? "not checked"
            : $"{report.InternetReachable} ({report.InternetLatency?.TotalMilliseconds:0} ms)");
        foreach (var note in report.Notes)
        {
            sb.AppendLine($"Note: {note}");
        }

        return sb.ToString();

        static string ErrorCatalogSafe(long bytes) => Core.Errors.ErrorCatalog.FormatBytes(bytes);
    }

    private static string BuildSettings(AppSettings settings)
    {
        // Settings contain no secrets by design; PFX path and thumbprint are not secret.
        return System.Text.Json.JsonSerializer.Serialize(settings, SettingsStore.JsonOptions) + Environment.NewLine;
    }

    private static string BuildConversions(AppSettings settings)
    {
        if (settings.RecentConversions.Count == 0)
        {
            return "No conversions recorded yet." + Environment.NewLine;
        }

        var sb = new StringBuilder();
        foreach (var item in settings.RecentConversions)
        {
            sb.AppendLine($"{item.Timestamp:yyyy-MM-dd HH:mm:ss}  {(item.Success ? "OK  " : "FAIL")}  {item.InputPath} → {item.OutputPath}" +
                          (item.Success
                              ? $"  [{ErrorCatalogSafe(item.OutputSizeBytes)}{(item.RequireAdministrator ? ", admin" : "")}{(item.CompressedWithUpx ? ", upx" : "")}]"
                              : $"  [{item.ErrorSummary}]"));
        }

        return sb.ToString();

        static string ErrorCatalogSafe(long bytes) => Core.Errors.ErrorCatalog.FormatBytes(bytes);
    }

    private string BuildLog()
    {
        var sb = new StringBuilder();
        foreach (var entry in _logger.Snapshot)
        {
            sb.AppendLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] [{entry.Source}] {entry.Message}");
        }

        if (sb.Length == 0)
        {
            sb.AppendLine("No in-memory log entries.");
        }

        if (!string.IsNullOrEmpty(_logger.LogFilePath) && File.Exists(_logger.LogFilePath))
        {
            sb.AppendLine();
            sb.AppendLine($"--- file log: {_logger.LogFilePath} ---");
            try
            {
                sb.Append(File.ReadAllText(_logger.LogFilePath));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(could not read file log: {ex.Message})");
            }
        }

        return sb.ToString();
    }

    private static string BuildApplicationInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"App version      : {typeof(DiagnosticExporter).Assembly.GetName().Version}");
        sb.AppendLine($"Runtime          : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"OS arch          : {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Process arch     : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"64-bit process   : {Environment.Is64BitProcess}");
        sb.AppendLine($"Current user     : {Environment.UserName}");
        sb.AppendLine($"Culture          : {System.Globalization.CultureInfo.CurrentCulture.Name}");
        sb.AppendLine($"LocalAppData     : {AppPaths.RootDirectory}");
        return sb.ToString();
    }
}
