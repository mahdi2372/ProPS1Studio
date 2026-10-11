namespace ProPS1Studio.Core.Errors;

public enum ErrorCode
{
    Unknown,
    Cancelled,
    InputNotFound,
    InputNotScript,
    InputEmpty,
    InputBinary,
    InputUnreadable,
    OutputDirectoryMissing,
    OutputDirectoryNotWritable,
    OutputFileExists,
    OutputMissing,
    OutputEmpty,
    OutputNotPe,
    OutputLocked,
    InsufficientDiskSpace,
    Ps2exeMissing,
    Ps2exeInstallFailed,
    Ps2exeOptionUnsupported,
    CompileFailed,
    CompileTimeout,
    AdminManifestMissing,
    UpxMissing,
    UpxIncompatible,
    UpxFailed,
    UpxVerificationFailed,
    NetworkFailure,
    DownloadIntegrityFailure,
    UacCancelled,
    LaunchFailed,
    SignFailed,
    CertificateNotFound,
    CertificateNoPrivateKey,
    SettingsCorrupt,
    InputTooLarge,
    InvalidOption,
    ExtraFileMissing,
    CscMissing,
    CscFailed,
    IconInvalid,
    VersionPatchFailed,
    SecretDetected,
}

/// <summary>An actionable application error: what happened plus what the user can do.</summary>
public sealed record AppError(ErrorCode Code, string Message, string Remediation, Exception? Exception = null)
{
    public override string ToString() => $"{Message}{Environment.NewLine}How to fix: {Remediation}";
}

public static class ErrorCatalog
{
    public static AppError InputNotFound(string path) => new(
        ErrorCode.InputNotFound,
        $"The selected script does not exist: {path}",
        "Check that the file has not been moved or deleted, then browse to it again.");

    public static AppError InputNotScript(string path) => new(
        ErrorCode.InputNotScript,
        $"The selected file is not a PowerShell script (.ps1): {path}",
        "Select a file with the .ps1 extension.");

    public static AppError InputEmpty(string path) => new(
        ErrorCode.InputEmpty,
        $"The selected script is empty (0 bytes): {path}",
        "Add PowerShell code to the script, or pick a different file.");

    public static AppError InputBinary(string path) => new(
        ErrorCode.InputBinary,
        $"The selected file looks like a binary file, not a text script: {path}",
        "Select a plain-text PowerShell (.ps1) script. If the script is valid but misdetected, re-save it as UTF-8.");

    public static AppError InputUnreadable(string path, string reason) => new(
        ErrorCode.InputUnreadable,
        $"The selected script could not be read: {reason}",
        "Close the file in other applications and check permissions, then try again.");

    public static AppError OutputDirectoryMissing(string dir) => new(
        ErrorCode.OutputDirectoryMissing,
        $"The output folder does not exist: {dir}",
        "Create the folder or choose a different output location.");

    public static AppError OutputDirectoryNotWritable(string dir, string reason) => new(
        ErrorCode.OutputDirectoryNotWritable,
        $"The output folder is not writable: {reason}",
        "Choose a folder you have permission to write to (for example inside your user profile).");

    public static AppError OutputFileExists(string path) => new(
        ErrorCode.OutputFileExists,
        $"An existing file would be overwritten: {path}",
        "Confirm the overwrite, or change the output filename.");

    public static AppError OutputMissing(string path) => new(
        ErrorCode.OutputMissing,
        $"Conversion reported success but the output file was not created: {path}",
        "Review the conversion log for compiler messages, verify the output folder, and retry.");

    public static AppError OutputEmpty(string path) => new(
        ErrorCode.OutputEmpty,
        $"The generated executable is empty (0 bytes): {path}",
        "The conversion did not complete correctly. Review the log, check antivirus quarantine, and retry.");

    public static AppError OutputNotPe(string path) => new(
        ErrorCode.OutputNotPe,
        $"The generated file is not a valid Windows executable: {path}",
        "The conversion output appears corrupted. Review the log and retry; if it persists, report a diagnostic export.");

    public static AppError OutputLocked(string path) => new(
        ErrorCode.OutputLocked,
        $"The file is locked by another process: {path}",
        "Close the file (running it, or holding it open in Explorer/debugger) and try again.");

    public static AppError InsufficientDiskSpace(string drive, long requiredBytes) => new(
        ErrorCode.InsufficientDiskSpace,
        $"Not enough free space on {drive}: about {FormatBytes(requiredBytes)} required.",
        "Free up disk space or select an output folder on another drive.");

    public static AppError Ps2exeMissing() => new(
        ErrorCode.Ps2exeMissing,
        "The PowerShell module 'ps2exe' is not installed.",
        "Open the Setup page and install ps2exe from the PowerShell Gallery (with your consent), or point Settings at an existing installation.");

    public static AppError Ps2exeInstallFailed(string reason) => new(
        ErrorCode.Ps2exeInstallFailed,
        $"Installing ps2exe failed: {reason}",
        "Check your internet connection and PowerShell Gallery access, then retry. You can also install manually: Install-Module ps2exe -Scope CurrentUser");

    public static AppError Ps2exeOptionUnsupported(string option) => new(
        ErrorCode.Ps2exeOptionUnsupported,
        $"The installed ps2exe version does not support the option '{option}'.",
        "Update ps2exe from the PowerShell Gallery, or disable the option that requires it.");

    public static AppError CompileFailed(int exitCode) => new(
        ErrorCode.CompileFailed,
        $"ps2exe failed to compile the script (exit code {exitCode}).",
        "Review the conversion log for the compiler error, fix the script, and retry.");

    public static AppError CompileTimeout(int seconds) => new(
        ErrorCode.CompileTimeout,
        $"The conversion did not finish within {seconds} seconds and was stopped.",
        "Close other heavy processes and retry, or increase the timeout in Settings.");

    public static AppError AdminManifestMissing(string path) => new(
        ErrorCode.AdminManifestMissing,
        $"The generated executable does not request administrator privileges: {path}",
        "The embedded manifest was not detected. Update ps2exe (it provides the -requireAdmin option), retry the conversion, and check the log.");

    public static AppError UpxMissing() => new(
        ErrorCode.UpxMissing,
        "UPX is not available.",
        "Install UPX from the Setup page (official release, hash-verified) or set the UPX path in Settings.");

    public static AppError UpxIncompatible(string reason) => new(
        ErrorCode.UpxIncompatible,
        $"UPX cannot pack this executable: {reason}",
        "Leave the file uncompressed. UPX does not support every executable format; compression is optional and never required.");

    public static AppError UpxFailed(string reason, string? backupPath) => new(
        ErrorCode.UpxFailed,
        $"UPX compression failed: {reason}",
        backupPath is null
            ? "The original executable was left untouched."
            : $"The original executable was restored from backup: {backupPath}");

    public static AppError UpxVerificationFailed(string path) => new(
        ErrorCode.UpxVerificationFailed,
        $"The compressed executable failed verification: {path}",
        "The original file was restored automatically. Leave the output uncompressed.");

    public static AppError NetworkFailure(string target, string reason) => new(
        ErrorCode.NetworkFailure,
        $"Network access to {target} failed: {reason}",
        "Check your internet connection, proxy, and firewall, then retry.");

    public static AppError DownloadIntegrityFailure(string target, string expectedHash, string actualHash) => new(
        ErrorCode.DownloadIntegrityFailure,
        $"Downloaded file from {target} failed hash verification.",
        $"Expected SHA-256 {expectedHash}, got {actualHash}. The download was discarded. Retry later or install the tool manually.");

    public static AppError UacCancelled() => new(
        ErrorCode.UacCancelled,
        "The User Account Control (UAC) prompt was cancelled.",
        "Run as Administrator was not performed because elevation was declined. Launch the executable normally if you do not want to elevate.");

    public static AppError LaunchFailed(string reason) => new(
        ErrorCode.LaunchFailed,
        $"Could not start the executable: {reason}",
        "Check that the file exists, is not blocked by antivirus/smart-screen policy, and that you have permission to run it.");

    public static AppError SignFailed(string reason) => new(
        ErrorCode.SignFailed,
        $"Authenticode signing failed: {reason}",
        "Verify the certificate, private key access, and timestamp server, then retry.");

    public static AppError CertificateNotFound(string thumbprint) => new(
        ErrorCode.CertificateNotFound,
        $"No certificate with thumbprint {thumbprint} was found in the current user or local machine stores.",
        "Import the certificate (with its private key) into the certificate store, or supply a PFX file in Settings.");

    public static AppError CertificateNoPrivateKey(string subject) => new(
        ErrorCode.CertificateNoPrivateKey,
        $"The certificate '{subject}' does not have a private key available.",
        "Use a certificate that includes the private key (for example a code-signing certificate you own).");

    public static AppError SettingsCorrupt(string path) => new(
        ErrorCode.SettingsCorrupt,
        $"The settings file was corrupt and has been reset: {path}",
        "Defaults were restored. Your previous settings file was kept with a .bak suffix.");

    public static AppError InputTooLarge(string path, long sizeBytes, long maxBytes) => new(
        ErrorCode.InputTooLarge,
        $"The file is too large to embed ({FormatBytes(sizeBytes)}, limit {FormatBytes(maxBytes)}): {path}",
        $"Keep the file under {FormatBytes(maxBytes)} or split it into smaller pieces.");

    public static AppError InvalidOption(string option, string detail) => new(
        ErrorCode.InvalidOption,
        $"The option '{option}' is not valid: {detail}",
        "Correct the value in the form and run the conversion again.");

    public static AppError ExtraFileMissing(string path) => new(
        ErrorCode.ExtraFileMissing,
        $"A file selected to bundle into the executable does not exist: {path}",
        "Restore the file, or remove it from the bundled-files list.");

    public static AppError CscMissing() => new(
        ErrorCode.CscMissing,
        "The C# compiler (csc.exe) that ships with the .NET Framework was not found.",
        "Windows 10/11 include it at %WINDIR%\\Microsoft.NET\\Framework64\\v4.0.30319. Repair the .NET Framework, or set PROPS_CSC_PATH to a csc.exe you trust.");

    public static AppError CscFailed(int exitCode) => new(
        ErrorCode.CscFailed,
        $"The C# compiler failed with exit code {exitCode}.",
        "Review the compiler output shown below, fix the reported problem, and convert again.");

    public static AppError IconInvalid(string path, string reason) => new(
        ErrorCode.IconInvalid,
        $"The selected icon could not be used ({reason}): {path}",
        "Pick a valid Windows .ico file (the format used by Explorer, not PNG or SVG).");

    public static AppError VersionPatchFailed(string path, string reason) => new(
        ErrorCode.VersionPatchFailed,
        $"Writing the version information into the executable failed: {reason}",
        "Retry the conversion; if it persists, choose an output folder that is not read-only and check whether antivirus locked the file.");

    public static AppError SecretDetected(string summary) => new(
        ErrorCode.SecretDetected,
        summary,
        "Remove or replace the hardcoded values with environment variables or a secret store, then convert again.");

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
