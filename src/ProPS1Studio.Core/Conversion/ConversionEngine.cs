using System.Text;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Protection;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Upx;

namespace ProPS1Studio.Core.Conversion;

/// <summary>
/// End-to-end conversion pipeline:
/// validate â†’ secret scan â†’ capability check â†’ ps2exe compile â†’ verify PE/manifest
/// â†’ optional UPX compression â†’ optional Authenticode signing.
/// Never executes the user's script.
/// </summary>
public sealed class ConversionEngine
{
    private const long RequiredFreeBytesFloor = 16L * 1024 * 1024;
    private const long ValidationReadCap = 4L * 1024 * 1024;

    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly IPs2exeCapabilityProbe _probe;
    private readonly IUpxPacker? _upx;
    private readonly IAuthenticodeSigner? _signer;
    private readonly ISecretScanner? _scanner;

    public ConversionEngine(
        IProcessRunner runner,
        IAppLogger logger,
        IPs2exeCapabilityProbe probe,
        IUpxPacker? upx = null,
        IAuthenticodeSigner? signer = null,
        ISecretScanner? scanner = null)
    {
        _runner = runner;
        _logger = logger;
        _probe = probe;
        _upx = upx;
        _signer = signer;
        _scanner = scanner;
    }

    public async Task<ConversionResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var warnings = new List<string>();
        var compilerOutput = new List<string>();
        var source = nameof(ConversionEngine);

        try
        {
            // 1. Validate the untrusted input.
            var validation = ScriptValidator.Validate(request.InputPath);
            if (!validation.IsValid)
            {
                return Fail(validation.Error!, warnings, compilerOutput, started);
            }

            warnings.AddRange(validation.Warnings);
            _logger.Write(LogLevel.Info, source,
                $"Validated '{request.InputPath}' ({validation.LengthBytes} bytes, {validation.LineCount} lines, {validation.Encoding}).");

            // 2. Prepare the output folder.
            var outputFull = Path.GetFullPath(request.OutputPath);
            var outputDir = Path.GetDirectoryName(outputFull);
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Environment.CurrentDirectory;
            }

            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception ex)
            {
                return Fail(ErrorCatalog.OutputDirectoryNotWritable(outputDir, ex.Message), warnings, compilerOutput, started);
            }

            if (File.Exists(outputFull))
            {
                if (!request.OverwriteOutput)
                {
                    return Fail(ErrorCatalog.OutputFileExists(outputFull), warnings, compilerOutput, started);
                }

                try
                {
                    File.Delete(outputFull);
                }
                catch (IOException ex)
                {
                    return Fail(ErrorCatalog.OutputLocked(outputFull + ": " + ex.Message), warnings, compilerOutput, started);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return Fail(ErrorCatalog.OutputLocked(outputFull + ": " + ex.Message), warnings, compilerOutput, started);
                }
            }

            // 3. Free disk space sanity check.
            var diskError = CheckDiskSpace(outputFull, request.InputPath);
            if (diskError is not null)
            {
                return Fail(diskError, warnings, compilerOutput, started);
            }

            // 4. Secret scan before anything is baked into an executable.
            IReadOnlyList<SecretFinding> findings = Array.Empty<SecretFinding>();
            if (request.EnableSecretScan && _scanner is not null)
            {
                var scanText = ReadBounded(request.InputPath);
                if (scanText is not null)
                {
                    var scan = _scanner.Scan(scanText);
                    findings = scan.Findings;
                    if (findings.Count > 0)
                    {
                        var summary = findings.Count == 1
                            ? $"1 potential secret found: {findings[0].RuleName} (line {findings[0].LineNumber})."
                            : $"{findings.Count} potential secrets found (first: {findings[0].RuleName} on line {findings[0].LineNumber}).";
                        _logger.Write(LogLevel.Warn, source, summary);
                        if (request.SecretFindingsAreFatal)
                        {
                            return new ConversionResult
                            {
                                Success = false,
                                Error = new AppError(
                                    ErrorCode.SignFailed,
                                    summary,
                                    "Remove or replace the hardcoded values with environment variables or a secret store, then convert again."),
                                SecretFindings = findings,
                                Warnings = warnings,
                                CompilerOutput = compilerOutput,
                                Duration = DateTime.UtcNow - started,
                            };
                        }

                        warnings.Add(summary + " Review the script before distributing the executable.");
                    }
                }
            }

            // 5. Probe the installed ps2exe version for the options it really supports.
            var capability = await _probe.ProbeAsync(request.Ps2exeModulePath, cancellationToken).ConfigureAwait(false);
            if (!capability.IsAvailable)
            {
                return Fail(ErrorCatalog.Ps2exeMissing(), warnings, compilerOutput, started, capability.Error);
            }

            if (request.RequireAdministrator && !capability.Supports("requireAdmin"))
            {
                return Fail(ErrorCatalog.Ps2exeOptionUnsupported("requireAdmin"), warnings, compilerOutput, started);
            }

            if (request.NoConsole && !capability.Supports("noConsole"))
            {
                return Fail(ErrorCatalog.Ps2exeOptionUnsupported("noConsole"), warnings, compilerOutput, started);
            }

            if (request.IconPath is not null && !capability.Supports("iconFile"))
            {
                warnings.Add("The installed ps2exe version does not support a custom icon; the default icon is used.");
            }

            // 6. Compile with ps2exe via a generated wrapper script.
            //    When encryption is requested, ps2exe compiles a generated loader
            //    (salt|IV|ciphertext blob; key split into masked seed + mask)
            //    instead of the original source.
            string? loaderDir = null;
            try
            {
                var compileInput = request.InputPath;
                if (request.EncryptScript)
                {
                    loaderDir = Path.Combine(AppPaths.TempDirectory, $"encrypt-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(loaderDir);
                    var loaderPath = Path.Combine(loaderDir, "loader.ps1");
                    var loader = ScriptEncryptor.BuildLoader(File.ReadAllBytes(request.InputPath));
                    File.WriteAllText(loaderPath, loader, new UTF8Encoding(false));
                    compileInput = loaderPath;

                    warnings.Add("Script source is encrypted inside the executable (AES-256-CBC). " +
                        "The decryption key is split across the loader (masked seed + mask), so there is no " +
                        "single readable key line - this hides the source from casual inspection (for " +
                        "example opening the exe in Notepad) - it is not copy protection.");
                    _logger.Write(LogLevel.Info, source, $"Encrypted loader created for '{request.InputPath}'.");
                }

                var script = Ps2exeScriptBuilder.Build(request, capability, compileInput);
                var compileResult = await RunCompilerAsync(request, script, compilerOutput, cancellationToken).ConfigureAwait(false);
                if (compileResult is not null)
                {
                    return Fail(compileResult, warnings, compilerOutput, started);
                }
            }
            finally
            {
                if (loaderDir is not null)
                {
                    TryDeleteDirectory(loaderDir);
                }
            }

            // 7. Verify what ps2exe actually produced.
            var verifyError = VerifyOutput(request, outputFull, out var pe, out var level, out var outputSize);
            if (verifyError is not null)
            {
                return Fail(verifyError, warnings, compilerOutput, started);
            }

            _logger.Write(LogLevel.Info, source,
                $"Compiled '{outputFull}' ({ErrorCatalog.FormatBytes(outputSize)}), level={level}, machine={pe!.MachineDisplayName}, dotnet={pe.IsDotNet}.");

            // 7b. Optional output protection: wrap the ps2exe result in an encrypted stub.
            //     The inner executable is never executed - it only exists as the stub's
            //     encrypted resource. On any protection failure the unprotected output is
            //     deleted so a failed build never leaves a file the user believes is protected.
            if (request.ProtectOutput)
            {
                var cscPath = BatCscLocator.Find();
                if (cscPath is null)
                {
                    TryDeleteFile(outputFull);
                    return Fail(ErrorCatalog.CscMissing(), warnings, compilerOutput, started);
                }

                warnings.Add(ExeProtector.HonestWarning);

                var protectDir = Path.Combine(AppPaths.TempDirectory, $"protect-{Guid.NewGuid():N}");
                Directory.CreateDirectory(protectDir);
                try
                {
                    var stubLevel = request.RequireAdministrator
                        ? BatExecutionLevel.RequireAdministrator
                        : BatExecutionLevel.AsInvoker;
                    var stubManifestPath = Path.Combine(protectDir, "stub.manifest");
                    await File.WriteAllTextAsync(stubManifestPath,
                        BatManifestBuilder.Build(stubLevel), new UTF8Encoding(false), cancellationToken)
                        .ConfigureAwait(false);

                    var stubIconPath = request.IconPath is not null && capability.Supports("iconFile")
                        ? Path.GetFullPath(request.IconPath)
                        : null;
                    var stubPath = Path.Combine(protectDir, "stub.exe");
                    var protectError = await ExeProtector.BuildProtectedExeAsync(
                        _runner, cscPath, protectDir, outputFull, stubPath, stubManifestPath, stubIconPath,
                        request.NoConsole, request.TimeoutSeconds, compilerOutput, _logger,
                        cancellationToken).ConfigureAwait(false);
                    if (protectError is not null)
                    {
                        TryDeleteFile(outputFull);
                        return Fail(protectError, warnings, compilerOutput, started);
                    }

                    try
                    {
                        File.Copy(stubPath, outputFull, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        TryDeleteFile(outputFull);
                        return Fail(ErrorCatalog.OutputLocked(outputFull + ": " + ex.Message), warnings, compilerOutput, started);
                    }

                    var stubVersionError = ApplyProtectionVersion(outputFull, request);
                    if (stubVersionError is not null)
                    {
                        TryDeleteFile(outputFull);
                        return Fail(stubVersionError, warnings, compilerOutput, started);
                    }

                    verifyError = VerifyOutput(request, outputFull, out pe, out level, out outputSize);
                    if (verifyError is not null)
                    {
                        TryDeleteFile(outputFull);
                        return Fail(verifyError, warnings, compilerOutput, started);
                    }

                    _logger.Write(LogLevel.Info, source,
                        $"Protected '{outputFull}' ({ErrorCatalog.FormatBytes(outputSize)}), level={level}, machine={pe!.MachineDisplayName}.");
                }
                finally
                {
                    TryDeleteDirectory(protectDir);
                }
            }

            // 8. Optional UPX compression.
            var compressed = false;
            long? sizeBeforeUpx = null;
            if (request.CompressWithUpx)
            {
                var upxResult = await CompressAsync(request, outputFull, pe!, warnings, cancellationToken).ConfigureAwait(false);
                if (upxResult.FatalError is not null)
                {
                    return Fail(upxResult.FatalError, warnings, compilerOutput, started);
                }

                compressed = upxResult.Compressed;
                sizeBeforeUpx = upxResult.SizeBefore;
            }

            // 9. Optional Authenticode signing (always after compression).
            var signed = false;
            string? signatureStatus = null;
            if (request.SignAfterConversion)
            {
                var signResult = await SignAsync(request, outputFull, warnings, cancellationToken).ConfigureAwait(false);
                if (signResult.FatalError is not null)
                {
                    return Fail(signResult.FatalError, warnings, compilerOutput, started);
                }

                signed = signResult.Signed;
                signatureStatus = signResult.Status;
            }

            // Final size/PE facts after optional stages.
            outputSize = new FileInfo(outputFull).Length;
            pe = PeFile.Read(outputFull) ?? pe;

            return new ConversionResult
            {
                Success = true,
                OutputPath = outputFull,
                OutputSizeBytes = outputSize,
                Pe = pe,
                ExecutionLevel = level,
                UpxCompressed = compressed,
                SizeBeforeUpx = sizeBeforeUpx,
                Signed = signed,
                SignatureStatus = signatureStatus,
                Encrypted = request.EncryptScript,
                Protected = request.ProtectOutput,
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
    }

    private async Task<AppError?> RunCompilerAsync(
        ConversionRequest request,
        string script,
        List<string> compilerOutput,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(AppPaths.TempDirectory, $"convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "convert.ps1");
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);

            var source = nameof(ConversionEngine);
            void OnLine(string line)
            {
                compilerOutput.Add(line);
                _logger.Write(LogLevel.Info, "ps2exe", line);
            }

            void OnError(string line)
            {
                compilerOutput.Add(line);
                _logger.Write(LogLevel.Warn, "ps2exe", line);
            }

            var result = await _runner.RunAsync(
                new ProcessSpec
                {
                    FileName = "powershell.exe",
                    Arguments = new[]
                    {
                        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", scriptPath,
                    },
                    WorkingDirectory = tempDir,
                    TimeoutMilliseconds = Math.Max(15, request.TimeoutSeconds) * 1000,
                },
                OnLine,
                OnError,
                cancellationToken).ConfigureAwait(false);

            if (result.FailedToStart)
            {
                return new AppError(ErrorCode.CompileFailed,
                    $"The PowerShell compiler process could not be started: {result.StartError}",
                    "Verify that PowerShell is available on this system, then retry.");
            }

            if (result.TimedOut)
            {
                return ErrorCatalog.CompileTimeout(Math.Max(15, request.TimeoutSeconds));
            }

            if (result.ExitCode != 0)
            {
                _logger.Write(LogLevel.Error, source, $"ps2exe exited with code {result.ExitCode}.");
                return ErrorCatalog.CompileFailed(result.ExitCode);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, nameof(ConversionEngine), $"Compiler stage failed: {ex.Message}");
            return new AppError(ErrorCode.CompileFailed, $"The conversion could not be started: {ex.Message}",
                "Check the output path and available disk space, then retry.");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private AppError? VerifyOutput(
        ConversionRequest request,
        string outputFull,
        out PeInfo? pe,
        out RequestedExecutionLevel level,
        out long outputSize)
    {
        pe = null;
        level = RequestedExecutionLevel.None;
        outputSize = 0;

        if (!File.Exists(outputFull))
        {
            return ErrorCatalog.OutputMissing(outputFull);
        }

        outputSize = new FileInfo(outputFull).Length;
        if (outputSize == 0)
        {
            return ErrorCatalog.OutputEmpty(outputFull);
        }

        pe = PeFile.Read(outputFull);
        if (!pe.IsValid)
        {
            return ErrorCatalog.OutputNotPe($"{outputFull} ({pe.Error})");
        }

        level = ManifestVerifier.DetectLevel(outputFull);
        if (request.RequireAdministrator && level != RequestedExecutionLevel.RequireAdministrator)
        {
            return ErrorCatalog.AdminManifestMissing(outputFull);
        }

        if (!request.RequireAdministrator && level == RequestedExecutionLevel.RequireAdministrator)
        {
            // ps2exe honored a stale/foreign manifest; surface it rather than ship a surprise.
            return new AppError(ErrorCode.AdminManifestMissing,
                $"The generated executable requests administrator privileges even though elevation was not requested: {outputFull}",
                "Re-run the conversion; if it persists, check for a stale output file or update ps2exe.");
        }

        return null;
    }

    private async Task<UpxStageResult> CompressAsync(
        ConversionRequest request,
        string outputFull,
        PeInfo pe,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (pe.IsDotNet)
        {
            warnings.Add("UPX cannot pack .NET executables, so the file was left uncompressed.");
            _logger.Write(LogLevel.Info, nameof(ConversionEngine), "UPX skipped: target is a .NET assembly.");
            return UpxStageResult.NotCompressed;
        }

        if (_upx is null)
        {
            var missing = ErrorCatalog.UpxMissing();
            return request.UpxFailureIsFatal
                ? UpxStageResult.Fatal(missing)
                : FailWarning(missing, warnings);
        }

        var packed = await _upx.PackAsync(outputFull, new UpxPackOptions
        {
            Force = request.UpxForce,
        }, cancellationToken).ConfigureAwait(false);

        if (packed.Success && packed.WasPacked)
        {
            _logger.Write(LogLevel.Info, nameof(ConversionEngine),
                $"UPX compressed {ErrorCatalog.FormatBytes(packed.OriginalSize)} â†’ {ErrorCatalog.FormatBytes(packed.PackedSize)}.");
            return new UpxStageResult(true, packed.OriginalSize, null);
        }

        var error = packed.Error ?? ErrorCatalog.UpxFailed("Unknown UPX failure.", packed.BackupPath);
        if (request.UpxFailureIsFatal)
        {
            return UpxStageResult.Fatal(error);
        }

        return FailWarning(error, warnings);
    }

    private async Task<SignStageResult> SignAsync(
        ConversionRequest request,
        string outputFull,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_signer is null)
        {
            var unavailableSigner = ErrorCatalog.SignFailed("The Authenticode signing service is not available.");
            return request.SigningFailureIsFatal
                ? SignStageResult.Fatal(unavailableSigner)
                : FailSignWarning(unavailableSigner, warnings);
        }

        if (request.SignRequest is null)
        {
            var noCertificate = ErrorCatalog.SignFailed("No certificate was configured for signing.");
            return request.SigningFailureIsFatal
                ? SignStageResult.Fatal(noCertificate)
                : FailSignWarning(noCertificate, warnings);
        }

        var signRequest = request.SignRequest with { TargetPath = outputFull };
        var result = await _signer.SignAsync(signRequest, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            if (result.Warning is not null)
            {
                warnings.Add(result.Warning);
            }

            _logger.Write(LogLevel.Info, nameof(ConversionEngine),
                $"Signed '{outputFull}' (status: {result.SignatureStatus}).");
            return new SignStageResult(true, result.SignatureStatus, null);
        }

        var error = result.Error ?? ErrorCatalog.SignFailed("Unknown signing failure.");
        return request.SigningFailureIsFatal ? SignStageResult.Fatal(error) : FailSignWarning(error, warnings);
    }

    private static UpxStageResult FailWarning(AppError error, List<string> warnings)
    {
        warnings.Add(error.ToString());
        return UpxStageResult.NotCompressed;
    }

    private static SignStageResult FailSignWarning(AppError error, List<string> warnings)
    {
        warnings.Add(error.ToString());
        return SignStageResult.NotSigned;
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

    /// <summary>Writes the version resource into the protection stub (the inner ps2exe
    /// executable already carries one, but it is encrypted inside the stub).</summary>
    private static AppError? ApplyProtectionVersion(string outputFull, ConversionRequest request)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(request.InputPath);
        var title = string.IsNullOrWhiteSpace(request.Title) ? fallbackTitle : request.Title.Trim();
        var product = string.IsNullOrWhiteSpace(request.Product) ? title : request.Product.Trim();
        var info = new BatVersionInfo(
            Title: title,
            Description: string.IsNullOrWhiteSpace(request.Description) ? string.Empty : request.Description.Trim(),
            Product: product,
            Company: string.IsNullOrWhiteSpace(request.Company) ? string.Empty : request.Company.Trim(),
            Copyright: string.IsNullOrWhiteSpace(request.Copyright) ? string.Empty : request.Copyright.Trim(),
            FileVersion: string.IsNullOrWhiteSpace(request.FileVersion) ? "1.0.0.0" : request.FileVersion.Trim(),
            OriginalFilename: Path.GetFileName(outputFull));

        try
        {
            BatVersionResource.Apply(outputFull, BatVersionResource.Build(info));
        }
        catch (Exception ex)
        {
            return ErrorCatalog.VersionPatchFailed(outputFull, ex.Message);
        }

        return null;
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
            // Best-effort cleanup of an output the conversion failed to produce.
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

    private ConversionResult Fail(AppError error, List<string> warnings, List<string> compilerOutput, DateTime started, string? extraDetail = null)
    {
        _logger.Write(LogLevel.Error, nameof(ConversionEngine), error.ToString());
        if (!string.IsNullOrWhiteSpace(extraDetail))
        {
            _logger.Write(LogLevel.Error, nameof(ConversionEngine), extraDetail);
        }

        return new ConversionResult
        {
            Success = false,
            Error = error,
            Warnings = warnings,
            CompilerOutput = compilerOutput,
            Duration = DateTime.UtcNow - started,
        };
    }

    private readonly record struct UpxStageResult(bool Compressed, long? SizeBefore, AppError? FatalError)
    {
        public static UpxStageResult NotCompressed => new(false, null, null);
        public static UpxStageResult Fatal(AppError error) => new(false, null, error);
    }

    private readonly record struct SignStageResult(bool Signed, string? Status, AppError? FatalError)
    {
        public static SignStageResult NotSigned => new(false, null, null);
        public static SignStageResult Fatal(AppError error) => new(false, null, error);
    }
}
