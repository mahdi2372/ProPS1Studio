using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Setup;

namespace ProPS1Studio.Integration.Tests;

/// <summary>
/// Real ps2exe compilations proving that EncryptScript keeps the original source
/// out of the produced executable. These tests never execute the converted script
/// or its loader; runtime behavior is covered by ScriptEncryptor unit tests.
/// </summary>
[TestFixture]
public class EncryptionPipelineTests
{
    private const string Marker = "PROPS1-ENCRYPT-E2E-MARKER-42x";

    private string _root = null!;
    private AppLogger _logger = null!;
    private ProcessRunner _runner = null!;
    private Ps2exeCapabilityProbe _probe = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"props1-enc-{Guid.NewGuid():N}");
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

    private ConversionEngine CreateEngine() => new(_runner, _logger, _probe, upx: null, signer: null, scanner: new SecretScanner());

    private string NewInputPath() => Path.Combine(_root, "input", $"in-{Guid.NewGuid():N}.ps1");

    private string NewOutputPath() => Path.Combine(_root, "output", $"out-{Guid.NewGuid():N}.exe");

    private async Task RequirePs2exeAsync()
    {
        var capability = await _probe.ProbeAsync();
        if (!capability.IsAvailable)
        {
            Assert.Ignore($"ps2exe is not installed: {capability.Error}");
        }
    }

    private static bool BytesContain(byte[] haystack, byte[] needle)
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
        Assert.That(BytesContain(exeBytes, Encoding.UTF8.GetBytes(marker)), Is.False, because + " (UTF-8)");
        Assert.That(BytesContain(exeBytes, Encoding.Unicode.GetBytes(marker)), Is.False, because + " (UTF-16LE)");
        Assert.That(BytesContain(exeBytes, Encoding.BigEndianUnicode.GetBytes(marker)), Is.False, because + " (UTF-16BE)");
    }

    private async Task<string> WriteMarkerScriptAsync()
    {
        var input = NewInputPath();
        var content = $"Write-Host '{Marker}'\n$password = '{Marker}'\n";
        await File.WriteAllTextAsync(input, content);
        return input;
    }

    [Test]
    public async Task Convert_EncryptScript_ProducesExeWithNoTraceOfSource()
    {
        await RequirePs2exeAsync();
        var input = await WriteMarkerScriptAsync();
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Encrypted, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("encrypted"));
        Assert.That(File.Exists(output), Is.True);

        AssertNoPlaintext(File.ReadAllBytes(output), Marker,
            "encrypted exe must not contain the original script text");
    }

    [Test]
    public async Task Convert_WithoutEncryptScript_Control_StillContainsPlaintext()
    {
        await RequirePs2exeAsync();
        var input = await WriteMarkerScriptAsync();
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = false,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Encrypted, Is.False);

        var exeBytes = File.ReadAllBytes(output);
        var sawPlain = BytesContain(exeBytes, Encoding.UTF8.GetBytes(Marker))
            || BytesContain(exeBytes, Encoding.Unicode.GetBytes(Marker));
        Assert.That(sawPlain, Is.True,
            "control build must still embed plaintext (proves the scan in the encrypted test is meaningful)");
    }

    [Test]
    public async Task Convert_EncryptScript_LeavesSourceScriptUntouched()
    {
        await RequirePs2exeAsync();
        var input = await WriteMarkerScriptAsync();
        var before = await File.ReadAllBytesAsync(input);
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(await File.ReadAllBytesAsync(input), Is.EqualTo(before), "the source file is never modified");
    }

    [Test]
    public async Task Convert_EncryptScript_DoesNotLeaveLoaderBehindInTemp()
    {
        await RequirePs2exeAsync();
        var input = await WriteMarkerScriptAsync();
        var output = NewOutputPath();
        var tempRoot = AppPaths.TempDirectory;

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptScript = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        var after = Directory.Exists(tempRoot) ? Directory.GetDirectories(tempRoot, "encrypt-*") : [];
        Assert.That(after, Is.Empty, "the temporary loader directory must be deleted after compilation");
    }
}
