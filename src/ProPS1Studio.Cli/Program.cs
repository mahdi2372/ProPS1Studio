using System;
using System.Globalization;
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
/// Commands: convert, bat, scan, doctor, preset, completion, help.
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
                "preset" => RunPreset(rest),
                "completion" => RunCompletion(rest),
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

    /// <summary>Expands positionals containing wildcards (* and ?) against the filesystem.</summary>
    private static List<string> ExpandGlobs(IEnumerable<string> positionals)
    {
        var result = new List<string>();
        foreach (var raw in positionals)
        {
            if (raw.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                result.Add(raw);
                continue;
            }

            var dir = Path.GetDirectoryName(raw);
            var pattern = string.IsNullOrWhiteSpace(dir) ? raw : Path.Combine(dir, Path.GetFileName(raw));
            foreach (var match in Directory.EnumerateFiles(string.IsNullOrWhiteSpace(dir) ? "." : dir, Path.GetFileName(raw)))
            {
                result.Add(match);
            }
        }

        return result;
    }

    private static bool IsQuiet(OptionSet options) => options.GetBool("quiet");

    // ---------------------------------------------------------------- convert

    private static async Task<int> RunConvertAsync(string[] args)
    {
        var options = OptionSet.Parse(args);
        var inputs = ExpandGlobs(options.Positionals);
        if (inputs.Count == 0)
        {
            return Usage("convert", "At least one .ps1 script is required.");
        }

        if (options.GetBool("watch"))
        {
            return await RunWatchAsync(inputs, options, (input, output) => ConvertOneAsync(input, output, options))
                .ConfigureAwait(false);
        }

        var exitCode = ExitOk;
        foreach (var rawInput in inputs)
        {
            var input = Path.GetFullPath(rawInput);
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Input file not found: {input}");
                exitCode = ExitFailure;
                continue;
            }

            var output = Path.GetFullPath(ResolveOutput(options, input));
            var code = await ConvertOneAsync(input, output, options).ConfigureAwait(false);
            if (code != ExitOk)
            {
                exitCode = code;
            }
        }

        return exitCode;
    }

    private static async Task<int> ConvertOneAsync(string input, string output, OptionSet options)
    {
        if (options.GetBool("validate-only"))
        {
            var validation = ScriptValidator.Validate(input);
            if (options.GetBool("json"))
            {
                Console.WriteLine(Json.Object(
                    ("valid", validation.IsValid),
                    ("error", validation.Error?.Message)));
            }
            else
            {
                Console.WriteLine($"{input}: {(validation.IsValid ? "valid" : "invalid - " + validation.Error?.Message)}");
            }

            return validation.IsValid ? ExitOk : ExitFailure;
        }

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

    /// <summary>Watches the input files and re-runs the action whenever one changes (polling).</summary>
    private static async Task<int> RunWatchAsync(
        List<string> inputs, OptionSet options, Func<string, string, Task<int>> action)
    {
        var interval = options.GetIntOrDefault("watch-interval", 2);
        var lastWrites = inputs.ToDictionary(
            raw => raw,
            raw => File.Exists(raw) ? new FileInfo(raw).LastWriteTimeUtc : DateTime.MinValue,
            StringComparer.Ordinal);

        Console.WriteLine($"Watching {inputs.Count} file(s); re-running on changes (interval {interval}s). Ctrl+C to stop.");

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval)).ConfigureAwait(false);
            foreach (var rawInput in inputs)
            {
                var input = Path.GetFullPath(rawInput);
                if (!File.Exists(input))
                {
                    continue;
                }

                var lastWrite = new FileInfo(input).LastWriteTimeUtc;
                if (lastWrite == lastWrites.GetValueOrDefault(rawInput))
                {
                    continue;
                }

                lastWrites[rawInput] = lastWrite;
                var output = Path.GetFullPath(ResolveOutput(options, input));
                await action(input, output).ConfigureAwait(false);
            }
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

        var inputs = ExpandGlobs(options.Positionals);
        if (inputs.Count == 0)
        {
            return Usage("bat", "At least one .bat/.cmd script is required.");
        }

        if (options.GetBool("watch"))
        {
            return await RunWatchAsync(inputs, options, (input, output) => BatOneAsync(input, output, options))
                .ConfigureAwait(false);
        }

        var exitCode = ExitOk;
        foreach (var rawInput in inputs)
        {
            var input = Path.GetFullPath(rawInput);
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Input file not found: {input}");
                exitCode = ExitFailure;
                continue;
            }

            var output = Path.GetFullPath(ResolveOutput(options, input));
            var code = await BatOneAsync(input, output, options).ConfigureAwait(false);
            if (code != ExitOk)
            {
                exitCode = code;
            }
        }

        return exitCode;
    }

    private static async Task<int> BatOneAsync(string input, string output, OptionSet options)
    {
        if (options.GetBool("validate-only"))
        {
            var validation = BatValidator.Validate(input);
            if (options.GetBool("json"))
            {
                Console.WriteLine(Json.Object(
                    ("valid", validation.IsValid),
                    ("error", validation.Error?.Message)));
            }
            else
            {
                Console.WriteLine($"{input}: {(validation.IsValid ? "valid" : "invalid - " + validation.Error?.Message)}");
            }

            return validation.IsValid ? ExitOk : ExitFailure;
        }

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
            var perFile = new Dictionary<string, SecretScanResult>(StringComparer.Ordinal);

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
                perFile[full] = result;

                if (!options.GetBool("json") && !IsQuiet(options))
                {
                    Console.WriteLine($"{full}: {result.Findings.Count} finding(s)");
                }

                foreach (var finding in result.Findings)
                {
                    if (allowLow && finding.Severity == SecretSeverity.Low)
                    {
                        continue;
                    }

                    if (options.GetBool("json"))
                    {
                        printed++;
                        hasFindings = true;
                        continue;
                    }

                    Console.WriteLine(
                        $"  [{finding.Severity}] {finding.RuleName} ({finding.RuleId}) " +
                        $"line {finding.LineNumber}, col {finding.Column}: {finding.MaskedValue}");
                    printed++;
                    hasFindings = true;
                }
            }

            if (options.GetBool("json"))
            {
                var files = string.Join(",", perFile.Select(kv =>
                    Json.Object(("file", kv.Key), ("findings", SecretScanner.ToJson(kv.Value)))));
                Console.WriteLine(Json.Object(("files", $"[{files}]"), ("count", printed)));
            }
            else if (IsQuiet(options))
            {
                // Errors only: nothing to print on success.
            }
            else
            {
                Console.WriteLine(hasFindings
                    ? $"{printed} finding(s) reported."
                    : "No secrets found.");
            }

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
            if (options.GetBool("json"))
            {
                Console.WriteLine(Json.Object(
                    ("os", report.OsDisplayVersion),
                    ("machine", report.MachineName),
                    ("elevated", report.IsElevated),
                    ("dotnet", report.DotNetRuntime),
                    ("powerShell", report.PowerShellVersion),
                    ("ps2exeAvailable", report.Ps2exe?.IsAvailable ?? false),
                    ("ps2exeVersion", report.Ps2exe?.ModuleVersion),
                    ("upxAvailable", report.Upx?.Available ?? false),
                    ("upxHashPinned", report.Upx?.MatchesPinnedHash),
                    ("notes", string.Join("; ", report.Notes))));
                return ExitOk;
            }

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

    // ----------------------------------------------------------------- preset

    private static int RunPreset(string[] args)
    {
        var options = OptionSet.Parse(args);
        var action = options.Positionals.FirstOrDefault() ?? "list";
        var services = CreateServices();
        try
        {
            var settings = services.Settings;
            switch (action)
            {
                case "list":
                    foreach (var preset in settings.Presets)
                    {
                        Console.WriteLine($"{preset.Name}  admin={preset.RequireAdmin} noConsole={preset.NoConsole} " +
                                          $"upx={preset.CompressWithUpx} encrypt={preset.Encrypt} " +
                                          $"protect={preset.ProtectOutput} sign={preset.SignAfterConversion} " +
                                          $"timeout={preset.TimeoutSeconds}");
                    }

                    return ExitOk;

                case "show":
                {
                    var name = options.Positionals.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return Usage("preset", "Usage: preset show <name>");
                    }

                    var preset = settings.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (preset is null)
                    {
                        Console.Error.WriteLine($"No such preset: {name}");
                        return ExitFailure;
                    }

                    Console.WriteLine(Json.Object(
                        ("name", preset.Name),
                        ("requireAdmin", preset.RequireAdmin),
                        ("noConsole", preset.NoConsole),
                        ("compressWithUpx", preset.CompressWithUpx),
                        ("encrypt", preset.Encrypt),
                        ("protectOutput", preset.ProtectOutput),
                        ("signAfterConversion", preset.SignAfterConversion),
                        ("timeoutSeconds", preset.TimeoutSeconds)));
                    return ExitOk;
                }

                case "add":
                {
                    var name = options.Positionals.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return Usage("preset", "Usage: preset add <name> [--admin] [--no-console] [--upx] [--encrypt|--no-encrypt] [--protect|--no-protect] [--sign|--no-sign] [--timeout n]");
                    }

                    var preset = new ConversionPreset
                    {
                        Name = name.Trim(),
                        RequireAdmin = options.GetBool("admin"),
                        NoConsole = options.GetBool("no-console"),
                        CompressWithUpx = options.GetBool("upx"),
                        Encrypt = options.GetBoolOrDefault("encrypt", true),
                        ProtectOutput = options.GetBoolOrDefault("protect", true),
                        SignAfterConversion = options.GetBool("sign"),
                        TimeoutSeconds = options.GetIntOrDefault("timeout", 180),
                    };

                    settings.Presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.Ordinal));
                    settings.Presets.Add(preset);
                    SaveSettings(services);
                    Console.WriteLine($"Preset '{preset.Name}' saved.");
                    return ExitOk;
                }

                case "remove":
                {
                    var name = options.Positionals.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return Usage("preset", "Usage: preset remove <name>");
                    }

                    var removed = settings.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (removed == 0)
                    {
                        Console.Error.WriteLine($"No such preset: {name}");
                        return ExitFailure;
                    }

                    SaveSettings(services);
                    Console.WriteLine($"Preset '{name}' removed.");
                    return ExitOk;
                }

                default:
                    return Usage("preset", $"Unknown action '{action}'. Use list, show, add or remove.");
            }
        }
        finally
        {
            services.Dispose();
        }
    }

    private static void SaveSettings(CliServices services)
    {
        var store = new SettingsStore(AppPaths.SettingsFilePath, services.Logger);
        store.Save(services.Settings);
    }

    // -------------------------------------------------------------- completion

    private static int RunCompletion(string[] args)
    {
        var options = OptionSet.Parse(args);
        var shell = (options.Positionals.FirstOrDefault() ?? "powershell").ToLowerInvariant();
        var commands = string.Join(" ", new[] { "convert", "bat", "scan", "doctor", "preset", "completion", "help" });
        var commonOptions = string.Join(" ", new[]
        {
            "-o --output", "--force", "--admin", "--no-console", "--upx --no-upx",
            "--encrypt --no-encrypt", "--protect --no-protect", "--timeout", "--scan-off",
            "--secrets-fatal --secrets-warn", "--sign --no-sign", "--sign-pfx", "--sign-thumbprint",
            "--sign-store", "--icon", "--title --description --company --product --version",
            "-v --verbose", "-q --quiet", "--json", "--watch", "--watch-interval", "--validate-only",
        });

        if (shell is "bash" or "zsh")
        {
            const string script = """
                _props1_cli_completion() {
                    local cur prev
                    cur="${COMP_WORDS[COMP_CWORD]}"
                    prev="${COMP_WORDS[COMP_CWORD-1]}"
                    local cmds="__COMMANDS__"
                    if [ "$COMP_CWORD" -eq 1 ]; then
                        COMPREPLY=( $(compgen -W "$cmds" -- "$cur") )
                        return 0
                    fi
                    case "$prev" in
                        -o|--output|--sign-pfx|--sign-thumbprint|--sign-store|--icon|--timeout|--watch-interval)
                            COMPREPLY=( $(compgen -f -- "$cur") ) ;;
                        *)
                            COMPREPLY=( $(compgen -W "__COMMON_OPTIONS__" -- "$cur") ) ;;
                    esac
                }
                complete -F _props1_cli_completion props1-cli
                """;
            Console.WriteLine(script.Replace("__COMMANDS__", commands).Replace("__COMMON_OPTIONS__", commonOptions));
            return ExitOk;
        }

        const string powerShell = """
            function ProPS1CliTabExpansion {
                param($command, $parameter, $wordToComplete)
                $commands = '__COMMANDS__'
                $options = '__COMMON_OPTIONS__'
                if ($parameter -eq '') {
                    return $commands.Split(' ') | Where-Object { $_ -like "$wordToComplete*" }
                }
                return $options.Split(' ') | Where-Object { $_ -like "$wordToComplete*" }
            }
            Register-ArgumentCompleter -Native -CommandName props1-cli -ScriptBlock {
                param($wordToComplete, $cursorPosition)
                $tokens = $wordToComplete -split '\s+'
                ProPS1CliTabExpansion -command $tokens[0] -parameter $tokens[1] -wordToComplete $wordToComplete
            }
            """;
        Console.WriteLine(powerShell.Replace("__COMMANDS__", commands).Replace("__COMMON_OPTIONS__", commonOptions));
        return ExitOk;
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
        if (options.GetBool("json"))
        {
            Console.WriteLine(Json.Object(
                ("success", result.Success),
                ("output", result.OutputPath),
                ("sizeBytes", result.OutputSizeBytes),
                ("upxCompressed", result.UpxCompressed),
                ("encrypted", result.Encrypted),
                ("protected", result.Protected),
                ("signed", result.Signed),
                ("error", result.Error?.ToString()),
                ("warnings", string.Join("; ", result.Warnings))));
            return result.Success ? ExitOk : ExitFailure;
        }

        if (!result.Success)
        {
            PrintFailure(result.Error);
            if (!IsQuiet(options))
            {
                PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
            }

            if (options.GetBool("verbose"))
            {
                PrintCompilerOutput(result.CompilerOutput);
            }

            return ExitFailure;
        }

        if (!IsQuiet(options))
        {
            Console.WriteLine($"OK  {result.OutputPath} ({ErrorCatalog.FormatBytes(result.OutputSizeBytes)}) in {result.Duration:h\\:mm\\:ss}");
            Console.WriteLine($"    PE: {result.Pe?.MachineDisplayName ?? "unknown"} | " +
                              $"UPX: {(result.UpxCompressed ? "yes" : "no")} | " +
                              $"encrypted: {(result.Encrypted ? "yes" : "no")} | " +
                              $"protected: {(result.Protected ? "yes" : "no")} | " +
                              $"signed: {(result.Signed ? "yes" : "no")}");
            PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
        }

        if (options.GetBool("verbose"))
        {
            PrintCompilerOutput(result.CompilerOutput);
        }

        return ExitOk;
    }

    private static int Report(BatConversionResult result, OptionSet options)
    {
        if (options.GetBool("json"))
        {
            Console.WriteLine(Json.Object(
                ("success", result.Success),
                ("output", result.OutputPath),
                ("sizeBytes", result.OutputSizeBytes),
                ("payloadEncrypted", result.PayloadEncrypted),
                ("protected", result.Protected),
                ("signed", result.Signed),
                ("error", result.Error?.ToString()),
                ("warnings", string.Join("; ", result.Warnings))));
            return result.Success ? ExitOk : ExitFailure;
        }

        if (!result.Success)
        {
            PrintFailure(result.Error);
            if (!IsQuiet(options))
            {
                PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
            }

            if (options.GetBool("verbose"))
            {
                PrintCompilerOutput(result.CompilerOutput);
            }

            return ExitFailure;
        }

        if (!IsQuiet(options))
        {
            Console.WriteLine($"OK  {result.OutputPath} ({ErrorCatalog.FormatBytes(result.OutputSizeBytes)}) in {result.Duration:h\\:mm\\:ss}");
            Console.WriteLine($"    PE: {result.Pe?.MachineDisplayName ?? "unknown"} | " +
                              $"payload: {(result.PayloadEncrypted ? "encrypted" : "plain")} | " +
                              $"protected: {(result.Protected ? "yes" : "no")} | " +
                              $"signed: {(result.Signed ? "yes" : "no")}");
            PrintWarningsAndSecrets(result.Warnings, result.SecretFindings);
        }

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
            // --no-timestamp opts out of RFC 3161 timestamping.
            TimestampServer = options.GetBool("no-timestamp") ? null : settings.TimestampServer,
            Description = options.GetString("sign-description"),
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
              preset list               Show saved conversion presets
              preset add <name> ...     Save a named conversion preset
              preset show <name>        Print one preset as JSON
              preset remove <name>      Delete a saved preset
              completion [bash|zsh|powershell]  Print shell tab-completion script
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
              --no-timestamp             Skip RFC 3161 timestamping during signing
              --sign-description <text>  Signing description (CP/CS path text)
              -v, --verbose               Print compiler output
              -q, --quiet                Only print errors
              --json                     Print machine-readable JSON results
              --watch                    Re-run on input file changes (polling)
              --watch-interval <sec>     Watch poll interval (default 2)
              --validate-only            Validate the input only; no conversion
              scan: --allow-low          Include Low-severity findings

            GLOB PATTERNS
              Inputs accept wildcards: 'props1-cli convert .\scripts\*.ps1' converts
              every matching script. With --watch each file re-runs on change.

            EXAMPLES
              props1-cli convert .\scripts\deploy.ps1 --admin --upx --encrypt -v
              props1-cli convert .\scripts\*.ps1 --force
              props1-cli convert .\scripts\deploy.ps1 --watch --watch-interval 3
              props1-cli convert .\scripts\deploy.ps1 --validate-only --json
              props1-cli bat .\setup.bat --protect --title "Setup" --version 1.2.3.4
              props1-cli scan .\scripts\deploy.ps1 --allow-low
              props1-cli preset add portable --encrypt --no-protect --timeout 300
              props1-cli doctor --json
              props1-cli completion bash | bash

            The PFX password is read only from the PROPS_SIGN_PFX_PASSWORD environment
            variable - never from the command line. Exit codes: 0 success, 1 failure,
            2 usage error.
            """);
    }
}

/// <summary>Minimal JSON output helpers for machine-readable CLI results.</summary>
internal static class Json
{
    public static string Object(params (string Key, object? Value)[] properties)
    {
        var parts = properties.Select(p => $"\"{Escape(p.Key)}\":{Value(p.Value)}");
        return "{" + string.Join(",", parts) + "}";
    }

    public static string Value(object? value) => value switch
    {
        null => "null",
        string text => $"\"{Escape(text)}\"",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => $"\"{Escape(value.ToString() ?? string.Empty)}\"",
    };

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                <= '\u001f' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }
}
