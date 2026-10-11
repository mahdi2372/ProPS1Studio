using NUnit.Framework;
using ProPS1Studio.Core.Errors;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class ErrorCatalogTests
{
    [TestCase(0, "0 B")]
    [TestCase(512, "512 B")]
    [TestCase(1023, "1023 B")]
    [TestCase(1024, "1 KB")]
    [TestCase(1536, "1.5 KB")]
    [TestCase(1048576, "1 MB")]
    [TestCase(1073741824, "1 GB")]
    [TestCase(-1, "-1 B")]
    public void FormatBytes_ProducesHumanReadableValues(long bytes, string expected)
    {
        Assert.That(ErrorCatalog.FormatBytes(bytes), Is.EqualTo(expected));
    }

    [Test]
    public void AppError_ToString_IncludesRemediation()
    {
        var error = ErrorCatalog.InputNotFound(@"C:\missing\script.ps1");
        var text = error.ToString();
        Assert.That(text, Does.Contain("How to fix:"));
        Assert.That(text, Does.Contain(error.Remediation));
        Assert.That(error.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public void AllFactoryErrors_HaveMessageAndRemediation()
    {
        var errors = new[]
        {
            ErrorCatalog.InputNotFound("a.ps1"),
            ErrorCatalog.InputNotScript("a.txt"),
            ErrorCatalog.InputEmpty("a.ps1"),
            ErrorCatalog.InputBinary("a.ps1"),
            ErrorCatalog.InputUnreadable("a.ps1", "locked"),
            ErrorCatalog.OutputDirectoryMissing(@"C:\out"),
            ErrorCatalog.OutputDirectoryNotWritable(@"C:\out", "denied"),
            ErrorCatalog.OutputFileExists(@"C:\out\a.exe"),
            ErrorCatalog.OutputMissing(@"C:\out\a.exe"),
            ErrorCatalog.OutputEmpty(@"C:\out\a.exe"),
            ErrorCatalog.OutputNotPe(@"C:\out\a.exe"),
            ErrorCatalog.OutputLocked(@"C:\out\a.exe"),
            ErrorCatalog.InsufficientDiskSpace("C:", 123456789),
            ErrorCatalog.Ps2exeMissing(),
            ErrorCatalog.Ps2exeInstallFailed("gallery down"),
            ErrorCatalog.Ps2exeOptionUnsupported("-requireAdmin"),
            ErrorCatalog.CompileFailed(1),
            ErrorCatalog.CompileTimeout(180),
            ErrorCatalog.AdminManifestMissing(@"C:\out\a.exe"),
            ErrorCatalog.UpxMissing(),
            ErrorCatalog.UpxIncompatible(".NET file"),
            ErrorCatalog.UpxFailed("crashed", null),
            ErrorCatalog.UpxFailed("crashed", @"C:\out\a.upxbak"),
            ErrorCatalog.UpxVerificationFailed(@"C:\out\a.exe"),
            ErrorCatalog.NetworkFailure("github.com", "timeout"),
            ErrorCatalog.DownloadIntegrityFailure("https://x/y.zip", "AAA", "BBB"),
            ErrorCatalog.UacCancelled(),
            ErrorCatalog.LaunchFailed("blocked"),
            ErrorCatalog.SignFailed("no cert"),
            ErrorCatalog.CertificateNotFound("ABCDEF"),
            ErrorCatalog.CertificateNoPrivateKey("CN=Test"),
            ErrorCatalog.SettingsCorrupt(@"C:\settings.json"),
            ErrorCatalog.InputTooLarge("a.bat", 100, 50),
            ErrorCatalog.InvalidOption("Console color", "expected two hex digits."),
            ErrorCatalog.ExtraFileMissing("data.bin"),
            ErrorCatalog.CscMissing(),
            ErrorCatalog.CscFailed(2),
            ErrorCatalog.IconInvalid("a.ico", "bad header"),
            ErrorCatalog.VersionPatchFailed("a.exe", "verification failed"),
            ErrorCatalog.SecretDetected("1 potential secret found."),
        };

        foreach (var error in errors)
        {
            Assert.That(error.Message, Is.Not.Empty, error.Code.ToString());
            Assert.That(error.Remediation, Is.Not.Empty, error.Code.ToString());
        }
    }
}
