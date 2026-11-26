using System;
using System.IO;
using System.Linq;
using System.Text;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Env;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Utilities;
using ProPS1Studio.Core.Upx;

namespace ProPS1Studio.Cli;

/// <summary>
/// Headless command-line interface for ProPS1 Studio. Shares the same Core engine,
/// settings and logs as the WPF app, so a CLI conversion is identical to a GUI conversion.
/// Commands: convert, bat, scan, doctor, help.
/// Exit codes: 0 success, 1 conversion/scan failure, 2 usage error.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return ExitOk;
        }

        var command = args[0];
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "convert" => await RunConvertAsync(rest).ConfigureAwait(false),
                "bat" => await RunBatAsync(rest).ConfigureAwait(false),
                "scan" => await RunScanAsync(rest).ConfigureAwait(false),
                "doctor" => await RunDoctorAsync(rest).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (OptionParseException ex)
        {
            Console.Error.WriteLine($"Usage error: {ex.Message}");
            Console.Error.WriteLine("Run 'props1-cli help' for usage.");
            return ExitUsage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Aborted.");
            return ExitFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            return ExitFailure;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'.");
        Console.Error.WriteLine("Run 'props1-cli help' for usage.");
        return ExitUsage;
    }

    // ---------------------------------------------------------------- convert

    private static async Task<int> RunConvertAsync(string[] args)
    {
        var options = OptionSet.Parse(args);
        if (options.Positionals.Count != 1)
        {
            return Usage("convert", "Exactly one .ps1 script is required.");
        }

        var input = Path.GetFullPath(options.Positionals[0]);
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input file not found: {input}");
            return ExitFailure;
        }

        var output = Path.GetFullPath(ResolveOutput(options, input));
        var services = CreateServices();
        try
        {
            var request = new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = options.GetBool("force") || !File.Exists(output),
                RequireAdministrator = options.GetBool("admin"),
                NoConsole = options.GetBool("no-console"),
                CompressWithUpx = options.GetBoolOrDefault("upx", services.Settings.CompressWithUpx),
                EncryptScript = options.GetBoolOrDefault("encrypt", services.Settings.EncryptScript),
                ProtectOutput = options.GetBoolOrDefault("protect", services.Settings.ProtectOutput),
                IconPath = options.GetString("icon"),
                Title = options.GetString("title"),
                Description = options.GetString("description"),
                Company = options.GetString("company"),
                Product = options.GetString("product"),
                FileVersion = options.GetString("version"),
                TimeoutSeconds = options.GetIntOrDefault("timeout", services.Settings.ConversionTimeoutSeconds),
                EnableSecretScan = !options.GetBool("scan-off"),
                SecretFindingsAreFatal = options.GetBoolOrDefault("secrets-fatal", true),
                SignAfterConversion = options.GetBoolOrDefault("sign", services.Settings.SignAfterConversion),
                SignRequest = BuildSignRequest(options, services.Settings),
            };

            Console.WriteLine($"Converting '{input}' -> '{output}'");
            var result = await services.Engine.ConvertAsync(request).ConfigureAwait(false);
            return Report(result, options);
        }
        finally
        {
            services.Dispose();
        }
    }

    // ------------------------------------------------------------------- bat

    private static async Task<int> RunBatAsync(string[] args)
    {
        var options = OptionSet.Parse(args);
        if (options.Positionals.Count != 1)
        {
            return Usage("bat", "Exactly one .bat/.cmd script is required.");
        }

        var input = Path.GetFullPath(options.Positionals[0]);
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input file not found: {input}");
            return ExitFailure;
        }

        var output = Path.GetFullPath(ResolveOutput(options, input));
        var services = CreateServices();
        try
        {
            var request = new BatConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = options.GetBool("force") || !File.Exists(output),
                ExecutionLevel = options.GetBool("admin")
                    ? BatExecutionLevel.RequireAdministrator
                    : BatExecutionLevel.AsInvoker,
                NoConsole = options.GetBool("no-console"),
                KeepTempFile = options.GetBool("keep-temp"),
                CompressPayload = options.GetBoolOrDefault("compress", true),
                EncryptPayload = options.GetBoolOrDefault("encrypt", true),
                ProtectOutput = options.GetBoolOrDefault("protect", services.Settings.ProtectOutput),
                Title = options.GetString("title"),
                Description = options.GetString("description"),
                Company = options.GetString("company"),
                Product = options.GetString("product"),
                FileVersion = options.GetString("version"),
                TimeoutSeconds = options.GetIntOrDefault("timeout", services.Settings.ConversionTimeoutSeconds),
                EnableSecretScan = !options.GetBool("scan-off"),
                SecretFindingsAreFatal = options.GetBoolOrDefault("secrets-fatal", true),
                SignAfterConversion = options.GetBoolOrDefault("sign", services.Settings.SignAfterConversion),
                SignRequest = BuildSignRequest(options, services.Settings),
            };

            Console.WriteLine($"Building '{input}' -> '{output}'");
            var result = await services.BatToExe.ConvertAsync(request).ConfigureAwait(false);
            return Report(result, options);
        }
        finally
        {
            services.Dispose();
        }
    }

    // ------------------------------------------------------------------- scan

    private static async Task<int> RunScanAsync(string[] args)
    {
        var options = OptionSet.Parse(args);
        if (options.Positionals.Count == 0)
        {
            return Usage("scan", "At least one file is required.");
        }

        var services = CreateServices();
        try
        {
            var patterns = new List<string>(services.Settings.ExtraSecretPatterns);
            foreach (var pattern in options.GetList("extra-pattern"))
            {
                patterns.Add(pattern);
            }

            var scanner = new SecretScanner();
            var allowLow = options.GetBool("allow-low");
            var printed = 0;
            var hasFindings = false;

            foreach (var path in options.Positionals)
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full))
                {
                    Console.Error.WriteLine($"File not found: {full}");
                    return ExitFailure;
                }

                var text = ReadBounded(full);
                if (text is null)
                {
                    Console.Error.WriteLine($"File is unreadable: {full}");
                    return ExitFailure;
                }

                var result = scanner.Scan(text, patterns.Count > 0 ? patterns : null);
                Console.WriteLine($"{full}: {result.Findings.Count} finding(s)");

                foreach (var finding in result.Findings)
                {
                    if (allowLow && finding.Severity == SecretSeverity.Low)
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"  [{finding.Severity}] {finding.RuleName} ({finding.RuleId}) " +
                        $"line {finding.LineNumber}, col {finding.Column}: {finding.MaskedValue}");
                    printed++;
                    hasFindings = true;
                }
            }

            Console.WriteLine(hasFindings
                ? $"{printed} finding(s) reported."
                : "No secrets found.");
            return hasFindings ? ExitFailure : ExitOk;
        }
        finally
        {
            services.Dispose();
        }
    }

    // --------------------------------------------------------------- doctor

    private static async Task<int> RunDoctorAsync(string[] args)
    {
        var options = OptionSet.Parse(args);
        var services = CreateServices();
        try
        {
            Console.WriteLine("ProPS1 Studio environment report");
            Console.WriteLine($"Generated:  {DateTime.Now:O}");
            Console.WriteLine();

            var report = await services.Environment.ProbeAsync().ConfigureAwait(false);
            Console.WriteLine($"OS:          {report.OsDisplayVersion} ({report.OsProduct}, build {report.OsBuild})");
            Console.WriteLine($"Machine:     {report.MachineName} ({(report.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
            Console.WriteLine($"Elevated:    {report.IsElevated}");
            Console.WriteLine($".NET:        {report.DotNetRuntime}");
            Console.WriteLine($"PowerShell:  {report.PowerShellVersion ?? "not detected"}");
            Console.WriteLine($"Disk:        {ErrorCatalog.FormatBytes(report.FreeDiskBytes)} free of {ErrorCatalog.FormatBytes(report.TotalDiskBytes)}");
            Console.WriteLine($"Internet:    {DescribeInternet(report)}");
            Console.WriteLine();

            Console.WriteLine($"ps2exe:      {DescribePs2exe(report.Ps2exe)}");
            Console.WriteLine($"UPX:         {DescribeUpx(report.Upx)}");
            Console.WriteLine();

            if (report.Notes.Count > 0)
            {
                Console.WriteLine("Notes:");
                foreach (var note in report.Notes)
                {
                    Console.WriteLine($"  - {note}");
                }

                Console.WriteLine();
            }

            return ExitOk;
        }
        finally
        {
            services.Dispose();
        }
    }

    private static string DescribeInternet(EnvironmentReport report)
    {
        if (report.InternetReachable is null)
        {
            return "unknown";
        }

        return report.InternetReachable.Value
            ? $"reachable ({report.InternetLatency?.TotalMilliseconds:0} ms)"
            : "unreachable";
    }

    private static string DescribePs2exe(Ps2exeCapability? ps2exe)
    {
        if (ps2exe is { IsAvailable: true })
        {
            return $"installed {ps2exe.ModuleVersion} at {ps2exe.ModulePath} ({ps2exe.Parameters.Count} parameters detected)";
        }

        return $"NOT installed ({ps2exe?.Error ?? "unknown"})";
    }

    private static string DescribeUpx(UpxAvailability? upx)
    {
        if (upx is { Available: true })
        {
            return $"installed {upx.Version ?? "unknown version"} at {upx.Path} (hash-pinned: {upx.MatchesPinnedHash})";
        }

        return $"NOT installed ({upx?.Error ?? "unknown"})";
    }

    // --------------------------------------------------------------- shared

    private static string ResolveOutput(OptionSet options, string input)
    {
        var output = options.GetString("output");
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.ChangeExtension(input, ".exe") ?? input + ".exe";
        }

        return output;
    }

    private static int Report(ConversionResult result, OptionSet options)
    {
        if (!result.Success)
        {
            PrintFailure(result.Error);
            PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
            if (options.GetBool("verbose"))
            {
                PrintCompilerOutput(result.CompilerOutput);
            }

            return ExitFailure;
        }

        Console.WriteLine($"OK  {result.OutputPath} ({ErrorCatalog.FormatBytes(result.OutputSizeBytes)}) in {result.Duration:h\\:mm\\:ss}");
        Console.WriteLine($"    PE: {result.Pe?.MachineDisplayName ?? "unknown"} | " +
                          $"UPX: {(result.UpxCompressed ? "yes" : "no")} | " +
                          $"encrypted: {(result.Encrypted ? "yes" : "no")} | " +
                          $"protected: {(result.Protected ? "yes" : "no")} | " +
                          $"signed: {(result.Signed ? "yes" : "no")}");
        PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
        if (options.GetBool("verbose"))
        {
            PrintCompilerOutput(result.CompilerOutput);
        }

        return ExitOk;
    }

    private static int Report(BatConversionResult result, OptionSet options)
    {
        if (!result.Success)
        {
            PrintFailure(result.Error);
            PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
            if (options.GetBool("verbose"))
            {
                PrintCompilerOutput(result.CompilerOutput);
            }

            return ExitFailure;
        }

        Console.WriteLine($"OK  {result.OutputPath} ({ErrorCatalog.FormatBytes(result.OutputSizeBytes)}) in {result.Duration:h\\:mm\\:ss}");
        Console.WriteLine($"    PE: {result.Pe?.MachineDisplayName ?? "unknown"} | " +
                          $"payload: {(result.PayloadEncrypted ? "encrypted" : "plain")} | " +
                          $"protected: {(result.Protected ? "yes" : "no")} | " +
                          $"signed: {(result.Signed ? "yes" : "no")}");
        PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
        if (options.GetBool("verbose"))
        {
            PrintCompilerOutput(result.CompilerOutput);
        }

        return ExitOk;
    }

    private static void PrintFailure(AppError? error)
    {
        if (error is null)
        {
            Console.Error.WriteLine("Conversion failed with an unknown error.");
            return;
        }

        Console.Error.WriteLine($"FAILED ({error.Code}): {error.Message}");
        Console.Error.WriteLine($"  How to fix: {error.Remediation}");
    }

    private static void PrintWarningsAndSecrets(IReadOnlyList<string> warnings, IReadOnlyList<SecretFinding> secrets)
    {
        foreach (var warning in warnings)
        {
            Console.WriteLine($"WARN  {warning}");
        }

        foreach (var secret in secrets)
        {
            Console.WriteLine($"SECRET  {secret.RuleName} (line {secret.LineNumber}): {secret.MaskedValue}");
        }
    }

    private static void PrintCompilerOutput(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Console.WriteLine("Compiler output:");
        foreach (var line in lines)
        {
            Console.WriteLine($"  | {line}");
        }
    }

    /// <summary>Builds a SignRequest from CLI overrides, falling back to the saved settings.</summary>
    private static SignRequest? BuildSignRequest(OptionSet options, AppSettings settings)
    {
        var pfx = options.GetString("sign-pfx") ?? settings.SignPfxPath;
        var thumbprint = options.GetString("sign-thumbprint") ?? settings.SignCertificateThumbprint;
        var store = options.GetString("sign-store") ?? settings.SignCertificateStoreLocation;

        if (!options.GetBoolOrDefault("sign", settings.SignAfterConversion) || (pfx is null && thumbprint is null))
        {
            return null;
        }

        return new SignRequest
        {
            // Overwritten by the engine on the request it actually signs.
            TargetPath = string.Empty,
            PfxPath = pfx,
            CertificateThumbprint = thumbprint,
            StoreLocation = string.IsNullOrWhiteSpace(store) ? "CurrentUser" : store,
            // The password is never passed via argv; it comes from the environment only.
            PfxPassword = Environment.GetEnvironmentVariable("PROPS_SIGN_PFX_PASSWORD"),
            TimestampServer = settings.TimestampServer,
        };
    }

    /// <summary>Reads up to 4 MB of text (the same cap the engines use for secret scanning).</summary>
    private static string? ReadBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var capacity = (int)Math.Min(stream.Length, 4L * 1024 * 1024);
            var buffer = new byte[capacity];
            var read = 0;
            while (read < capacity)
            {
                var n = stream.Read(buffer, read, capacity - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class CliServices : IDisposable
    {
        public CliServices(
            AppLogger logger,
            AppSettings settings,
            ConversionEngine engine,
            BatToExeEngine batToExe,
            EnvironmentProbe environment)
        {
            Logger = logger;
            Settings = settings;
            Engine = engine;
            BatToExe = batToExe;
            Environment = environment;
        }

        public AppLogger Logger { get; }
        public AppSettings Settings { get; }
        public ConversionEngine Engine { get; }
        public BatToExeEngine BatToExe { get; }
        public EnvironmentProbe Environment { get; }

        public void Dispose()
        {
            Logger.Flush();
            Logger.Dispose();
        }
    }

    private static CliServices CreateServices()
    {
        var logger = new AppLogger(AppPaths.LogsDirectory);
        var settings = new SettingsStore(AppPaths.SettingsFilePath, logger).Load();
        var runner = new ProcessRunner();
        var probe = new Ps2exeCapabilityProbe(runner, logger);
        var upx = new UpxManager(runner, logger);
        var signer = new AuthenticodeSigner(runner, logger);
        var scanner = new SecretScanner();

        return new CliServices(
            logger,
            settings,
            new ConversionEngine(runner, logger, probe, upx, signer, scanner),
            new BatToExeEngine(runner, logger, signer, scanner),
            new EnvironmentProbe(runner, probe, upx));
    }

    private static int Usage(string command, string detail)
    {
        Console.Error.WriteLine($"Usage error ({command}): {detail}");
        Console.Error.WriteLine("Run 'props1-cli help' for full usage.");
        return ExitUsage;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ProPS1 Studio command-line interface (props1-cli)

            USAGE
              props1-cli <command> [options]

            COMMANDS
              convert <script.ps1>      Compile a PowerShell script to .exe (ps2exe)
              bat <script.bat|cmd>     Build a batch script into a .NET launcher .exe
              scan <file> [file2 ...]   Scan files for hardcoded secrets (masked output)
              doctor                     Print an environment health report
              help                       Show this help

            COMMON OPTIONS
              -o, --output <path>        Output path (default: input with .exe extension)
              --force                    Overwrite an existing output file
              --admin                    Embed a requireAdministrator UAC manifest
              --no-console               NoConsole mode (windowed output)
              --upx / --no-upx           Compress with UPX (default: from settings)
              --encrypt / --no-encrypt   Encrypt the embedded payload (default: on)
              --protect / --no-protect   Wrap the exe in the encrypted anti-debug stub
              --compress / --no-compress Bundle payload compression (bat only, default: on)
              --timeout <seconds>        Conversion timeout
              --scan-off                 Do not scan for hardcoded secrets
              --secrets-fatal / --secrets-warn  Secret findings: fail or just warn
              --sign / --no-sign         Sign after conversion (default: from settings)
              --sign-pfx <path>          PFX file for signing
              --sign-thumbprint <hex>    Certificate thumbprint for signing
              --sign-store <location>    Certificate store location (default: CurrentUser)
              --icon <path>              Custom .ico for ps2exe conversions
              --title/--description/--company/--product/--version  Version-resource fields
              -v, --verbose               Print compiler output

            EXAMPLES
              props1-cli convert .\scripts\deploy.ps1 --admin --upx --encrypt -v
              props1-cli bat .\setup.bat --protect --title "Setup" --version 1.2.3.4
              props1-cli scan .\scripts\deploy.ps1 --allow-low
              props1-cli doctor

            The PFX password is read only from the PROPS_SIGN_PFX_PASSWORD environment
            variable - never from the command line. Exit codes: 0 success, 1 failure,
            2 usage error.
            """);
    }
}
