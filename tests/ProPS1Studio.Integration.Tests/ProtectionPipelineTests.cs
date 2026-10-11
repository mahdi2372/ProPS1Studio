using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Protection;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Setup;

namespace ProPS1Studio.Integration.Tests;

/// <summary>
/// Real conversions proving that ProtectOutput wraps the finished executable in
/// the encrypted anti-debug stub: the launcher/ps2exe payload only exists inside
/// the stub as ciphertext. Nothing here ever executes the produced executable.
/// </summary>
[TestFixture]
public class ProtectionPipelineTests
{
    private const string BatMarker = "PROPS1-BAT-PROTECT-MARKER-55x";
    private const string PsMarker = "PROPS1-PS-PROTECT-MARKER-66x";
    private const string LauncherSalt = "ProPS1.BatToExe.v1";

    private string _root = null!;
    private AppLogger _logger = null!;
    private ProcessRunner _runner = null!;
    private Ps2exeCapabilityProbe _probe = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"props1-protect-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "input"));
        Directory.CreateDirectory(Path.Combine(_root, "output"));
        _logger = new AppLogger(Path.Combine(_root, "logs"));
        _runner = new ProcessRunner();
        _probe = new Ps2exeCapabilityProbe(_runner, _logger);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _logger.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private ConversionEngine CreatePsEngine() =>
        new(_runner, _logger, _probe, upx: null, signer: null, scanner: new SecretScanner());

    private BatToExeEngine CreateBatEngine() =>
        new(_runner, _logger, signer: null, scanner: new SecretScanner());

    private string NewInputPath(string extension) =>
        Path.Combine(_root, "input", $"in-{Guid.NewGuid():N}{extension}");

    private string NewOutputPath() =>
        Path.Combine(_root, "output", $"out-{Guid.NewGuid():N}.exe");

    private async Task RequirePs2exeAsync()
    {
        var capability = await _probe.ProbeAsync();
        if (!capability.IsAvailable)
        {
            Assert.Ignore($"ps2exe is not installed: {capability.Error}");
        }
    }

    private static void RequireCsc()
    {
        if (BatCscLocator.Find() is null)
        {
            Assert.Ignore("csc.exe (the .NET Framework C# compiler) is not available on this machine.");
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertNoPlaintext(byte[] exeBytes, string marker, string because)
    {
        Assert.That(Contains(exeBytes, Encoding.UTF8.GetBytes(marker)), Is.False, because + " (UTF-8)");
        Assert.That(Contains(exeBytes, Encoding.Unicode.GetBytes(marker)), Is.False, because + " (UTF-16LE)");
        Assert.That(Contains(exeBytes, Encoding.BigEndianUnicode.GetBytes(marker)), Is.False, because + " (UTF-16BE)");
    }

    private static bool SawPlaintext(byte[] exeBytes, string marker) =>
        Contains(exeBytes, Encoding.UTF8.GetBytes(marker)) ||
        Contains(exeBytes, Encoding.Unicode.GetBytes(marker));

    [Test]
    public async Task Convert_ProtectOutput_ProducesProtectedExeWithoutPlaintextSource()
    {
        await RequirePs2exeAsync();
        RequireCsc();

        var input = NewInputPath(".ps1");
        await File.WriteAllTextAsync(input, $"Write-Host '{PsMarker}'\n$password = '{PsMarker}'\n");
        var output = NewOutputPath();

        var result = await CreatePsEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = false,
            ProtectOutput = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Protected, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("obfuscation"),
            "the honest scope warning must be surfaced");
        Assert.That(File.Exists(output), Is.True);

        Assert.That(result.Pe!.IsValid, Is.True, result.Pe.Error);
        Assert.That(result.Pe.IsDotNet, Is.True, "the protection stub is a .NET assembly");
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.AsInvoker));
        Assert.That(ManifestVerifier.DetectLevel(output), Is.EqualTo(RequestedExecutionLevel.AsInvoker));

        AssertNoPlaintext(File.ReadAllBytes(output), PsMarker,
            "protection alone must keep the script source out of the executable");
    }

    [Test]
    public async Task Convert_WithoutProtectOutput_Control_StillContainsPlaintext()
    {
        await RequirePs2exeAsync();
        RequireCsc();

        var input = NewInputPath(".ps1");
        await File.WriteAllTextAsync(input, $"Write-Host '{PsMarker}'\n$password = '{PsMarker}'\n");
        var output = NewOutputPath();

        var result = await CreatePsEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = false,
            ProtectOutput = false,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Protected, Is.False);
        Assert.That(result.Warnings, Has.No.Member(ExeProtector.HonestWarning));

        Assert.That(SawPlaintext(File.ReadAllBytes(output), PsMarker), Is.True,
            "control build must embed plaintext (proves the scan in the protected test is meaningful)");
    }

    [Test]
    public async Task Convert_ProtectOutput_RequireAdministrator_StubCarriesAdminManifest()
    {
        await RequirePs2exeAsync();
        RequireCsc();

        var input = NewInputPath(".ps1");
        await File.WriteAllTextAsync(input, "Write-Host 'admin protect'\n");
        var output = NewOutputPath();

        var result = await CreatePsEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            RequireAdministrator = true,
            EncryptScript = false,
            ProtectOutput = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
        Assert.That(ManifestVerifier.DetectLevel(output), Is.EqualTo(RequestedExecutionLevel.RequireAdministrator),
            "the protection stub must carry the requested execution level");
    }

    [Test]
    public async Task Convert_ProtectOutput_DoesNotLeaveTemporaryDirectoryBehind()
    {
        await RequirePs2exeAsync();
        RequireCsc();

        var input = NewInputPath(".ps1");
        await File.WriteAllTextAsync(input, "Write-Host 'temp cleanup protect'\n");
        var output = NewOutputPath();

        var result = await CreatePsEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = false,
            ProtectOutput = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        var tempRoot = AppPaths.TempDirectory;
        var after = Directory.Exists(tempRoot) ? Directory.GetDirectories(tempRoot, "protect-*") : [];
        Assert.That(after, Is.Empty, "the temporary protect directory must be deleted after compilation");
    }

    [Test]
    public async Task BatToExe_ProtectOutput_ProducesProtectedExeWithoutLauncherSalt()
    {
        RequireCsc();

        var input = NewInputPath(".bat");
        File.WriteAllText(input, $"@echo off\r\necho {BatMarker}\r\n");
        var output = NewOutputPath();

        var result = await CreateBatEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptPayload = false,
            ProtectOutput = true,
            Title = "Protect Test",
            FileVersion = "9.8.7.6",
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Protected, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("obfuscation"),
            "the honest scope warning must be surfaced");
        Assert.That(File.Exists(output), Is.True);

        Assert.That(result.Pe!.IsValid, Is.True, result.Pe.Error);
        Assert.That(result.Pe.IsDotNet, Is.True, "the protection stub is a .NET assembly");
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.AsInvoker));
        Assert.That(ManifestVerifier.DetectLevel(output), Is.EqualTo(RequestedExecutionLevel.AsInvoker));

        var versionInfo = FileVersionInfo.GetVersionInfo(output);
        Assert.That(versionInfo.FileVersion, Is.EqualTo("9.8.7.6"),
            "the version resource must be written into the protection stub");

        var exeBytes = File.ReadAllBytes(output);
        Assert.That(Contains(exeBytes, Encoding.Unicode.GetBytes(LauncherSalt)), Is.False,
            "the launcher salt literal proves the launcher is encrypted inside the stub");
        AssertNoPlaintext(exeBytes, BatMarker, "the batch source must not survive in the protected output");
    }

    [Test]
    public async Task BatToExe_WithoutProtectOutput_Control_KeepsLauncherSalt()
    {
        RequireCsc();

        var input = NewInputPath(".bat");
        File.WriteAllText(input, $"@echo off\r\necho {BatMarker}\r\n");
        var output = NewOutputPath();

        var result = await CreateBatEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptPayload = false,
            ProtectOutput = false,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Protected, Is.False);

        var exeBytes = File.ReadAllBytes(output);
        Assert.That(Contains(exeBytes, Encoding.Unicode.GetBytes(LauncherSalt)), Is.True,
            "control build embeds the launcher salt (proves the absence check is meaningful)");
    }
}
