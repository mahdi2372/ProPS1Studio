using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Protection;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.BatToExe;

/// <summary>
/// End-to-end batch-to-executable conversion:
/// validate → option checks → secret scan → payload encode → generated launcher source →
/// compile with the .NET Framework csc.exe → verify PE/manifest → patch the VERSION resource
/// → copy to the output path → optional Authenticode signing.
/// Never executes the batch script and never runs the produced executable.
/// </summary>
public sealed class BatToExeEngine
{
    private const long RequiredFreeBytesFloor = 16L * 1024 * 1024;
    private const long ExtraFileMaxBytes = 4L * 1024 * 1024;
    private const int ValidationReadCap = 4 * 1024 * 1024;

    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly IAuthenticodeSigner? _signer;
    private readonly ISecretScanner? _scanner;
    private readonly string? _cscPathOverride;

    public BatToExeEngine(
        IProcessRunner runner,
        IAppLogger logger,
        IAuthenticodeSigner? signer = null,
        ISecretScanner? scanner = null,
        string? cscPathOverride = null)
    {
        _runner = runner;
        _logger = logger;
        _signer = signer;
        _scanner = scanner;
        _cscPathOverride = cscPathOverride;
    }

    public async Task<BatConversionResult> ConvertAsync(
        BatConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = DateTime.UtcNow;
        var warnings = new List<string>();
        var compilerOutput = new List<string>();
        const string source = nameof(BatToExeEngine);
        string? tempDir = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Validate the untrusted batch file.
            var validation = BatValidator.Validate(request.InputPath);
            if (!validation.IsValid)
            {
                return Fail(validation.Error!, warnings, compilerOutput, started);
            }

            warnings.AddRange(validation.Warnings);
            _logger.Write(LogLevel.Info, source,
                $"Validated '{request.InputPath}' ({validation.LengthBytes} bytes, {validation.LineCount} lines, {validation.Encoding}).");

            // 2. Validate options, bundled files, and the icon before touching the filesystem.
            var scriptFileName = ComputeScriptFileName(request.InputPath);
            var optionError = ValidateOptions(request, scriptFileName, warnings);
            if (optionError is not null)
            {
                return Fail(optionError, warnings, compilerOutput, started);
            }

            // 3. Prepare the output location (the previous output is only replaced on success).
            var outputFull = Path.GetFullPath(request.OutputPath);
            var outputDir = Path.GetDirectoryName(outputFull);
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Environment.CurrentDirectory;
            }

            var outputError = PrepareOutput(outputFull, outputDir, request.OverwriteOutput, warnings);
            if (outputError is not null)
            {
                return Fail(outputError, warnings, compilerOutput, started);
            }

            if (string.Equals(Path.GetFullPath(request.InputPath), outputFull, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(ErrorCatalog.InvalidOption("Output",
                    "the output file must not be the same as the input batch file."),
                    warnings, compilerOutput, started);
            }

            if (!outputFull.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("The output file does not use the .exe extension: it will run from a command prompt, but Explorer will not start it with a double-click.");
            }

            // 4. Free disk space sanity check.
            var diskError = CheckDiskSpace(outputFull, request.InputPath);
            if (diskError is not null)
            {
                return Fail(diskError, warnings, compilerOutput, started);
            }

            // 5. Secret scan before anything is baked into an executable.
            var (scanFatal, findings) = ScanSecrets(request, warnings);
            if (scanFatal is not null)
            {
                return Fail(scanFatal, warnings, compilerOutput, started, findings);
            }

            // 6. Encode the batch payload. One random AES secret per conversion, shared by the
            // payload and every bundled file, and never stored inside their Base64 blobs.
            byte[] inputBytes;
            try
            {
                inputBytes = File.ReadAllBytes(request.InputPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(ErrorCatalog.InputUnreadable(request.InputPath, ex.Message), warnings, compilerOutput, started, findings);
            }

            var aesSecret = request.EncryptPayload ? BatPayloadCodec.CreateAesSecret() : null;
            var payload = BatPayloadCodec.Encode(inputBytes, request.CompressPayload, request.Passphrase, request.EncryptPayload, aesSecret);

            // 7. Encode the bundled files.
            var extraFiles = new List<BatEmbeddedFile>();
            foreach (var extraPath in request.ExtraFilePaths)
            {
                var fullExtraPath = Path.GetFullPath(extraPath);
                byte[] extraBytes;
                try
                {
                    extraBytes = File.ReadAllBytes(fullExtraPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail(ErrorCatalog.InputUnreadable(fullExtraPath, ex.Message), warnings, compilerOutput, started, findings);
                }

                var encoded = BatPayloadCodec.Encode(extraBytes, request.CompressPayload, request.Passphrase, request.EncryptPayload, aesSecret);
                extraFiles.Add(new BatEmbeddedFile(Path.GetFileName(fullExtraPath), encoded.Mode, encoded.Data));
            }

            // 8. Locate the C# compiler.
            var cscPath = BatCscLocator.Find(_cscPathOverride);
            if (cscPath is null)
            {
                return Fail(ErrorCatalog.CscMissing(), warnings, compilerOutput, started, findings);
            }

            // 9. Generate the launcher source and manifest in a private temp directory.
            tempDir = Path.Combine(AppPaths.TempDirectory, $"bat2exe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            if (request.KeepTempFile)
            {
                warnings.Add($"The build files were kept for inspection at: {tempDir}");
            }

            var sourcePath = Path.Combine(tempDir, "launcher.cs");
            var manifestPath = Path.Combine(tempDir, "launcher.manifest");
            var stagePath = Path.Combine(tempDir, "stage.exe");
            var iconFull = request.IconPath is null ? null : Path.GetFullPath(request.IconPath);

            var launcherSource = BatLauncherSource.Build(new BatLauncherOptions(
                ScriptFileName: scriptFileName,
                Payload: payload,
                Passphrase: request.Passphrase ?? string.Empty,
                ExtraFiles: extraFiles,
                NoConsole: request.NoConsole,
                CaptureOutputLog: request.CaptureOutputLog,
                KeepConsoleOpen: request.KeepConsoleOpen,
                ForwardArguments: request.ForwardArguments,
                RunFromTempDirectory: request.RunFromTempDirectory,
                KeepTempFile: request.KeepTempFile,
                ConsoleTitle: string.IsNullOrWhiteSpace(request.ConsoleTitle) ? null : request.ConsoleTitle,
                ConsoleColorHex: string.IsNullOrWhiteSpace(request.ConsoleColorHex) ? null : request.ConsoleColorHex));

            await File.WriteAllTextAsync(sourcePath, launcherSource, new UTF8Encoding(true), cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(manifestPath, BatManifestBuilder.Build(request.ExecutionLevel),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            // 10. Compile with the .NET Framework csc (never executes the result).
            var compileError = await CompileAsync(
                cscPath, tempDir, sourcePath, manifestPath, stagePath, iconFull,
                request, compilerOutput, cancellationToken).ConfigureAwait(false);
            if (compileError is not null)
            {
                return Fail(compileError, warnings, compilerOutput, started, findings);
            }

            // 11. Verify what the compiler actually produced.
            var expectedLevel = MapLevel(request.ExecutionLevel);
            var verifyError = VerifyStage(stagePath, expectedLevel, out var pe);
            if (verifyError is not null)
            {
                return Fail(verifyError, warnings, compilerOutput, started, findings);
            }

            // 11b. Optional output protection: wrap the launcher in an encrypted stub.
            //      The stub becomes the staged executable, so the version resource and the
            //      copy below operate on it. The inner launcher is never executed.
            if (request.ProtectOutput)
            {
                warnings.Add(ExeProtector.HonestWarning);

                var stubPath = Path.Combine(tempDir, "stub.exe");
                var protectError = await ExeProtector.BuildProtectedExeAsync(
                    _runner, cscPath, tempDir, stagePath, stubPath, manifestPath, iconFull,
                    request.NoConsole, request.TimeoutSeconds, compilerOutput, _logger,
                    cancellationToken).ConfigureAwait(false);
                if (protectError is not null)
                {
                    return Fail(protectError, warnings, compilerOutput, started, findings);
                }

                var stubVerifyError = VerifyStage(stubPath, expectedLevel, out var stubPe);
                if (stubVerifyError is not null)
                {
                    return Fail(stubVerifyError, warnings, compilerOutput, started, findings);
                }

                pe = stubPe;
                stagePath = stubPath;
                _logger.Write(LogLevel.Info, source,
                    $"Protected '{stagePath}' with an encrypted anti-debug stub ({ErrorCatalog.FormatBytes(new FileInfo(stagePath).Length)}).");
            }

            // 12. Write the version resource into the staged executable.
            var versionInfo = BuildVersionInfo(request, scriptFileName, outputFull);
            var versionError = ApplyVersion(stagePath, versionInfo);
            if (versionError is not null)
            {
                return Fail(versionError, warnings, compilerOutput, started, findings);
            }

            _logger.Write(LogLevel.Info, source,
                $"Compiled '{outputFull}' ({ErrorCatalog.FormatBytes(new FileInfo(stagePath).Length)}), level={expectedLevel}, machine={pe!.MachineDisplayName}.");

            // 13. Publish the staged executable to the requested output path.
            try
            {
                File.Copy(stagePath, outputFull, overwrite: true);
            }
            catch (IOException ex)
            {
                return Fail(ErrorCatalog.OutputLocked($"{outputFull}: {ex.Message}"), warnings, compilerOutput, started, findings);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail(ErrorCatalog.OutputDirectoryNotWritable(outputDir, ex.Message), warnings, compilerOutput, started, findings);
            }

            // 14. Optional Authenticode signing (always last, on the final file).
            var signed = false;
            string? signatureStatus = null;
            if (request.SignAfterConversion)
            {
                var signError = await SignAsync(request, outputFull, warnings, cancellationToken).ConfigureAwait(false);
                if (signError is not null)
                {
                    TryDeleteFile(outputFull);
                    return Fail(signError, warnings, compilerOutput, started, findings);
                }

                signed = _lastSignSucceeded;
                signatureStatus = _lastSignatureStatus;
            }

            // Final facts after the copy (and optional signing).
            var outputSize = new FileInfo(outputFull).Length;
            var finalPe = PeFile.Read(outputFull);

            return new BatConversionResult
            {
                Success = true,
                OutputPath = outputFull,
                OutputSizeBytes = outputSize,
                Pe = finalPe.IsValid ? finalPe : pe,
                ExecutionLevel = expectedLevel,
                PayloadCompressed = BatPayloadCodec.IsCompressed(payload.Mode),
                PayloadObfuscated = BatPayloadCodec.IsXorObfuscated(payload.Mode),
                PayloadEncrypted = BatPayloadCodec.IsAesEncrypted(payload.Mode),
                Protected = request.ProtectOutput,
                ExtraFileCount = extraFiles.Count,
                Signed = signed,
                SignatureStatus = signatureStatus,
                SecretFindings = findings,
                Warnings = warnings,
                CompilerOutput = compilerOutput,
                Duration = DateTime.UtcNow - started,
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Write(LogLevel.Warn, source, "Conversion cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, source, $"Unexpected conversion failure: {ex}");
            return Fail(
                new AppError(ErrorCode.Unknown, $"Unexpected error: {ex.Message}",
                    "Review the log and export diagnostics from the Diagnostics page, then retry."),
                warnings, compilerOutput, started);
        }
        finally
        {
            if (tempDir is not null && !request.KeepTempFile)
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }

    private (AppError? Fatal, List<SecretFinding> Findings) ScanSecrets(
        BatConversionRequest request,
        List<string> warnings)
    {
        const string source = nameof(BatToExeEngine);
        if (!request.EnableSecretScan || _scanner is null)
        {
            return (null, new List<SecretFinding>());
        }

        var scanText = ReadBounded(request.InputPath);
        if (scanText is null)
        {
            return (null, new List<SecretFinding>());
        }

        var scan = _scanner.Scan(scanText);
        if (scan.Findings.Count == 0)
        {
            return (null, new List<SecretFinding>());
        }

        var findings = new List<SecretFinding>(scan.Findings);
        var summary = scan.Findings.Count == 1
            ? $"1 potential secret found: {scan.Findings[0].RuleName} (line {scan.Findings[0].LineNumber})."
            : $"{scan.Findings.Count} potential secrets found (first: {scan.Findings[0].RuleName} on line {scan.Findings[0].LineNumber}).";
        _logger.Write(LogLevel.Warn, source, summary);

        if (request.SecretFindingsAreFatal)
        {
            return (ErrorCatalog.SecretDetected(summary), findings);
        }

        warnings.Add(summary + " Review the batch file before distributing the executable.");
        return (null, findings);
    }

    private AppError? ValidateOptions(BatConversionRequest request, string scriptFileName, List<string> warnings)
    {
        if (request.ConsoleColorHex is { } color &&
            !Regex.IsMatch(color, "^[0-9A-Fa-f]{2}$"))
        {
            return ErrorCatalog.InvalidOption("Console color",
                "expected exactly two hex digits (background then foreground, each 0-15).");
        }

        if (request.ConsoleTitle is { } title && title.Any(char.IsControl))
        {
            return ErrorCatalog.InvalidOption("Console title", "it must not contain control characters.");
        }

        if (request.FileVersion is { } fileVersion &&
            !Regex.IsMatch(fileVersion, @"^\d{1,5}(\.\d{1,5}){0,3}$"))
        {
            return ErrorCatalog.InvalidOption("File version",
                "expected up to four dot-separated numbers, for example 1.0.0.0.");
        }

        if (!string.IsNullOrEmpty(request.Passphrase))
        {
            warnings.Add("The passphrase only obfuscates the embedded script (XOR). Anyone with the executable can decode it; it is not copy protection.");
            if (request.Passphrase.Length < 8)
            {
                warnings.Add("The passphrase is shorter than 8 characters, which makes the obfuscation easier to guess.");
            }
        }

        if (request.NoConsole)
        {
            warnings.Add("In no-console mode the script cannot ask for keyboard input (pause / set /p).");
            if (!request.CaptureOutputLog)
            {
                warnings.Add("In no-console mode the script's console output is discarded unless an output log is captured.");
            }

            if (request.KeepConsoleOpen)
            {
                warnings.Add("'Keep console open' has no effect in no-console mode.");
            }
        }

        if (request.CaptureOutputLog && !request.NoConsole)
        {
            warnings.Add("The output log is only captured in no-console mode; without it the script's output goes straight to the console.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extraPath in request.ExtraFilePaths)
        {
            if (string.IsNullOrWhiteSpace(extraPath))
            {
                return ErrorCatalog.InvalidOption("Bundled files", "an empty file path was provided.");
            }

            if (!File.Exists(extraPath))
            {
                return ErrorCatalog.ExtraFileMissing(extraPath);
            }

            long extraLength;
            try
            {
                extraLength = new FileInfo(extraPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ErrorCatalog.InputUnreadable(extraPath, ex.Message);
            }

            if (extraLength > ExtraFileMaxBytes)
            {
                return ErrorCatalog.InputTooLarge(extraPath, extraLength, ExtraFileMaxBytes);
            }

            var name = Path.GetFileName(extraPath);
            if (string.IsNullOrEmpty(name))
            {
                return ErrorCatalog.InvalidOption("Bundled files", $"'{extraPath}' does not name a file.");
            }

            if (string.Equals(name, scriptFileName, StringComparison.OrdinalIgnoreCase))
            {
                return ErrorCatalog.InvalidOption("Bundled files",
                    $"'{name}' collides with the embedded batch script's file name.");
            }

            if (!seenNames.Add(name))
            {
                return ErrorCatalog.InvalidOption("Bundled files", $"'{name}' is bundled more than once.");
            }
        }

        if (request.IconPath is { } iconPath)
        {
            var iconError = ValidateIcon(iconPath);
            if (iconError is not null)
            {
                return iconError;
            }
        }

        return null;
    }

    private static AppError? ValidateIcon(string iconPath)
    {
        if (!File.Exists(iconPath))
        {
            return ErrorCatalog.IconInvalid(iconPath, "the file does not exist");
        }

        if (!string.Equals(Path.GetExtension(iconPath), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCatalog.IconInvalid(iconPath, "it is not a .ico file");
        }

        try
        {
            using var stream = File.OpenRead(iconPath);
            var header = new byte[4];
            var read = stream.Read(header, 0, 4);
            if (read < 4 || header[0] != 0x00 || header[1] != 0x00 || header[2] != 0x01 || header[3] != 0x00)
            {
                return ErrorCatalog.IconInvalid(iconPath, "it does not start with a Windows icon header");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorCatalog.IconInvalid(iconPath, ex.Message);
        }

        return null;
    }

    private static AppError? PrepareOutput(string outputFull, string outputDir, bool overwrite, List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorCatalog.OutputDirectoryNotWritable(outputDir, ex.Message);
        }

        if (File.Exists(outputFull) && !overwrite)
        {
            return ErrorCatalog.OutputFileExists(outputFull);
        }

        return null;
    }

    private async Task<AppError?> CompileAsync(
        string cscPath,
        string tempDir,
        string sourcePath,
        string manifestPath,
        string stagePath,
        string? iconFullPath,
        BatConversionRequest request,
        List<string> compilerOutput,
        CancellationToken cancellationToken)
    {
        const string source = nameof(BatToExeEngine);
        var arguments = new List<string>
        {
            "/nologo",
            "/target:" + (request.NoConsole ? "winexe" : "exe"),
            "/optimize+",
            "/platform:anycpu",
            "/r:System.dll",
            "/out:" + stagePath,
            "/win32manifest:" + manifestPath,
        };
        if (iconFullPath is not null)
        {
            arguments.Add("/win32icon:" + iconFullPath);
        }

        arguments.Add(sourcePath);

        _logger.Write(LogLevel.Info, source, $"Compiling the launcher with '{cscPath}'.");

        void OnLine(string line)
        {
            compilerOutput.Add(line);
            _logger.Write(LogLevel.Info, "csc", line);
        }

        void OnError(string line)
        {
            compilerOutput.Add(line);
            _logger.Write(LogLevel.Warn, "csc", line);
        }

        var result = await _runner.RunAsync(
            new ProcessSpec
            {
                FileName = cscPath,
                Arguments = arguments,
                WorkingDirectory = tempDir,
                TimeoutMilliseconds = Math.Max(15, request.TimeoutSeconds) * 1000,
            },
            OnLine,
            OnError,
            cancellationToken).ConfigureAwait(false);

        if (result.FailedToStart)
        {
            return ErrorCatalog.CscMissing();
        }

        if (result.TimedOut)
        {
            return ErrorCatalog.CompileTimeout(Math.Max(15, request.TimeoutSeconds));
        }

        if (result.ExitCode != 0)
        {
            _logger.Write(LogLevel.Error, source, $"csc exited with code {result.ExitCode}.");
            return ErrorCatalog.CscFailed(result.ExitCode);
        }

        return null;
    }

    private static AppError? VerifyStage(string stagePath, RequestedExecutionLevel expectedLevel, out PeInfo? pe)
    {
        pe = null;
        if (!File.Exists(stagePath))
        {
            return ErrorCatalog.OutputMissing(stagePath);
        }

        var size = new FileInfo(stagePath).Length;
        if (size == 0)
        {
            return ErrorCatalog.OutputEmpty(stagePath);
        }

        pe = PeFile.Read(stagePath);
        if (!pe.IsValid)
        {
            return ErrorCatalog.OutputNotPe($"{stagePath} ({pe.Error})");
        }

        if (!pe.IsDotNet)
        {
            return new AppError(
                ErrorCode.OutputNotPe,
                $"The generated executable is not a .NET assembly, so the launcher was not compiled correctly: {stagePath}",
                "Review the compiler output shown below and retry the conversion.");
        }

        var actualLevel = ManifestVerifier.DetectLevel(stagePath);
        if (actualLevel != expectedLevel)
        {
            return new AppError(
                ErrorCode.AdminManifestMissing,
                $"The embedded manifest of the generated executable requests '{actualLevel}' but '{expectedLevel}' was requested: {stagePath}",
                "Review the compiler output (the /win32manifest option) and retry the conversion.");
        }

        return null;
    }

    private static BatVersionInfo BuildVersionInfo(
        BatConversionRequest request,
        string scriptFileName,
        string outputFull)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(scriptFileName);
        var title = string.IsNullOrWhiteSpace(request.Title) ? fallbackTitle : request.Title.Trim();
        var product = string.IsNullOrWhiteSpace(request.Product) ? title : request.Product.Trim();

        return new BatVersionInfo(
            Title: title,
            Description: string.IsNullOrWhiteSpace(request.Description) ? string.Empty : request.Description.Trim(),
            Product: product,
            Company: string.IsNullOrWhiteSpace(request.Company) ? string.Empty : request.Company.Trim(),
            Copyright: string.IsNullOrWhiteSpace(request.Copyright) ? string.Empty : request.Copyright.Trim(),
            FileVersion: string.IsNullOrWhiteSpace(request.FileVersion) ? "1.0.0.0" : request.FileVersion.Trim(),
            OriginalFilename: Path.GetFileName(outputFull));
    }

    private AppError? ApplyVersion(string stagePath, BatVersionInfo info)
    {
        const string source = nameof(BatToExeEngine);
        try
        {
            var blob = BatVersionResource.Build(info);
            BatVersionResource.Apply(stagePath, blob);
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, source, $"Version resource failed: {ex.Message}");
            return ErrorCatalog.VersionPatchFailed(stagePath, ex.Message);
        }

        try
        {
            var actual = FileVersionInfo.GetVersionInfo(stagePath);
            var mismatches = new List<string>();
            if (!string.Equals(actual.FileDescription ?? string.Empty, info.Title, StringComparison.Ordinal))
            {
                mismatches.Add($"FileDescription '{actual.FileDescription}' != '{info.Title}'");
            }

            if (!string.Equals(actual.ProductName ?? string.Empty, info.Product, StringComparison.Ordinal))
            {
                mismatches.Add($"ProductName '{actual.ProductName}' != '{info.Product}'");
            }

            if (info.Company.Length > 0 &&
                !string.Equals(actual.CompanyName ?? string.Empty, info.Company, StringComparison.Ordinal))
            {
                mismatches.Add($"CompanyName '{actual.CompanyName}' != '{info.Company}'");
            }

            if (!string.Equals(actual.FileVersion ?? string.Empty, info.FileVersion, StringComparison.Ordinal))
            {
                mismatches.Add($"FileVersion '{actual.FileVersion}' != '{info.FileVersion}'");
            }

            if (mismatches.Count > 0)
            {
                var reason = "verification failed: " + string.Join("; ", mismatches);
                _logger.Write(LogLevel.Error, source, $"Version resource {reason}");
                return ErrorCatalog.VersionPatchFailed(stagePath, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, source, $"Version verification failed: {ex.Message}");
            return ErrorCatalog.VersionPatchFailed(stagePath, ex.Message);
        }

        return null;
    }

    private bool _lastSignSucceeded;
    private string? _lastSignatureStatus;

    private static AppError? HandleSignFailure(AppError error, bool fatal, List<string> warnings)
    {
        if (fatal)
        {
            return error;
        }

        warnings.Add(error.ToString());
        return null;
    }

    private async Task<AppError?> SignAsync(
        BatConversionRequest request,
        string outputFull,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        _lastSignSucceeded = false;
        _lastSignatureStatus = null;

        if (_signer is null)
        {
            var unavailable = ErrorCatalog.SignFailed("The Authenticode signing service is not available.");
            return HandleSignFailure(unavailable, request.SigningFailureIsFatal, warnings);
        }

        if (request.SignRequest is null)
        {
            var noCertificate = ErrorCatalog.SignFailed("No certificate was configured for signing.");
            return HandleSignFailure(noCertificate, request.SigningFailureIsFatal, warnings);
        }

        var signRequest = request.SignRequest with { TargetPath = outputFull };
        var result = await _signer.SignAsync(signRequest, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _lastSignSucceeded = true;
            _lastSignatureStatus = result.SignatureStatus;
            if (result.Warning is not null)
            {
                warnings.Add(result.Warning);
            }

            _logger.Write(LogLevel.Info, nameof(BatToExeEngine),
                $"Signed '{outputFull}' (status: {result.SignatureStatus}).");
            return null;
        }

        var error = result.Error ?? ErrorCatalog.SignFailed("Unknown signing failure.");
        if (!request.SigningFailureIsFatal)
        {
            warnings.Add(error.ToString());
            return null;
        }

        return error;
    }

    private static RequestedExecutionLevel MapLevel(BatExecutionLevel level) => level switch
    {
        BatExecutionLevel.RequireAdministrator => RequestedExecutionLevel.RequireAdministrator,
        BatExecutionLevel.HighestAvailable => RequestedExecutionLevel.HighestAvailable,
        _ => RequestedExecutionLevel.AsInvoker,
    };

    private static string ComputeScriptFileName(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        var name = Path.GetFileName(inputPath);
        if (string.IsNullOrEmpty(name) || name.IndexOf('%') >= 0)
        {
            return "script" + extension;
        }

        return name;
    }

    private static AppError? CheckDiskSpace(string outputFull, string inputPath)
    {
        try
        {
            var root = Path.GetPathRoot(outputFull);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            long inputLength = 0;
            try
            {
                inputLength = new FileInfo(inputPath).Length;
            }
            catch
            {
                // Validation already succeeded; treat size as unknown.
            }

            var required = Math.Max(RequiredFreeBytesFloor, inputLength * 4);
            if (drive.AvailableFreeSpace < required)
            {
                return ErrorCatalog.InsufficientDiskSpace(root, required);
            }

            return null;
        }
        catch
        {
            // If the drive cannot be queried, let the conversion attempt proceed.
            return null;
        }
    }

    private static string? ReadBounded(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = Math.Min(stream.Length, ValidationReadCap);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, (int)length);
            if (read < buffer.Length)
            {
                Array.Resize(ref buffer, read);
            }

            return Encoding.UTF8.GetString(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private BatConversionResult Fail(
        AppError error,
        List<string> warnings,
        List<string> compilerOutput,
        DateTime started,
        IReadOnlyList<SecretFinding>? findings = null)
    {
        _logger.Write(LogLevel.Error, nameof(BatToExeEngine), error.ToString());
        return new BatConversionResult
        {
            Success = false,
            Error = error,
            SecretFindings = findings ?? Array.Empty<SecretFinding>(),
            Warnings = warnings,
            CompilerOutput = compilerOutput,
            Duration = DateTime.UtcNow - started,
        };
    }
}
