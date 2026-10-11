using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;

namespace ProPS1Studio.Integration.Tests;

/// <summary>
/// Real BatToExeEngine runs that invoke the .NET Framework csc.exe on this
/// machine. Nothing here ever executes the produced executable. Missing
/// prerequisites (no csc, no repo icon) skip instead of failing.
/// </summary>
[TestFixture]
public class BatToExePipelineTests
{
    private string _root = null!;
    private AppLogger _logger = null!;
    private ProcessRunner _runner = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"props1-bat-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "input"));
        Directory.CreateDirectory(Path.Combine(_root, "output"));
        _logger = new AppLogger(Path.Combine(_root, "logs"));
        _runner = new ProcessRunner();
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

    private BatToExeEngine CreateEngine() =>
        new(_runner, _logger, signer: null, scanner: new SecretScanner());

    private string NewInputPath() => Path.Combine(_root, "input", $"in-{Guid.NewGuid():N}.bat");

    private string NewOutputPath() => Path.Combine(_root, "output", $"out-{Guid.NewGuid():N}.exe");

    private static void RequireCsc()
    {
        if (BatCscLocator.Find() is null)
        {
            Assert.Ignore("csc.exe (the .NET Framework C# compiler) is not available on this machine.");
        }
    }

    private static string WriteBatch(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1-bat-src-{Guid.NewGuid():N}.bat");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>IMAGE_SUBSYSTEM value: 2 = Windows GUI, 3 = console CUI.</summary>
    private static ushort ReadSubsystem(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        stream.Position = 0x3C;
        var peOffset = reader.ReadUInt32();
        stream.Position = peOffset + 24 + 68; // PE signature + COFF header + optional header subsystem
        return reader.ReadUInt16();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProPS1Studio.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsBase64Char(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';

    /// <summary>
    /// Pulls the longest UTF-16LE run of Base64 characters out of the exe bytes.
    /// A string literal in the compiled launcher survives as UTF-16, so this recovers
    /// exactly what an attacker would extract from the produced executable.
    /// </summary>
    private static string? ExtractUtf16Base64Blob(byte[] haystack, int minChars)
    {
        var run = new StringBuilder();
        string? best = null;
        var bestLength = 0;
        for (var i = 0; i + 1 < haystack.Length; i += 2)
        {
            var c = (char)(haystack[i] | (haystack[i + 1] << 8));
            if (IsBase64Char(c) && haystack[i + 1] == 0)
            {
                run.Append(c);
                continue;
            }

            if (run.Length >= minChars && run.Length > bestLength)
            {
                best = run.ToString();
                bestLength = run.Length;
            }

            run.Clear();
        }

        if (run.Length >= minChars && run.Length > bestLength)
        {
            best = run.ToString();
        }

        return best;
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    [Test]
    public async Task Convert_SimpleBatch_ProducesValidDotNetExeWithVersionResource()
    {
        RequireCsc();

        var input = NewInputPath();
        const string script = "@echo off\r\necho hello from ProPS1 Studio\r\n";
        File.WriteAllText(input, script);
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            Title = "My Batch Tool",
            Description = "Batch launcher test",
            Product = "ProPS1 Batch",
            Company = "ACME Inc.",
            FileVersion = "1.2.3.4",
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(File.Exists(output), Is.True, "output executable was produced");
        Assert.That(result.OutputSizeBytes, Is.GreaterThan(2048));
        Assert.That(result.Pe!.IsValid, Is.True, result.Pe.Error);
        Assert.That(result.Pe.IsDotNet, Is.True, "the launcher must be a .NET assembly");
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.AsInvoker));
        Assert.That(ManifestVerifier.DetectLevel(output), Is.EqualTo(RequestedExecutionLevel.AsInvoker));
        Assert.That(result.PayloadCompressed, Is.True, "compress is on by default");
        Assert.That(result.PayloadObfuscated, Is.False);
        Assert.That(result.PayloadEncrypted, Is.True, "AES payload encryption is on by default");
        Assert.That(result.Signed, Is.False);
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(result.Warnings, Is.Empty);
        Assert.That(File.Exists(input), Is.True, "the source script is never consumed");

        var versionInfo = FileVersionInfo.GetVersionInfo(output);
        Assert.That(versionInfo.FileVersion, Is.EqualTo("1.2.3.4"));
        Assert.That(versionInfo.FileDescription, Is.EqualTo("My Batch Tool"));
        Assert.That(versionInfo.ProductName, Is.EqualTo("ProPS1 Batch"));
        Assert.That(versionInfo.CompanyName, Is.EqualTo("ACME Inc."));

        // The batch source must not appear anywhere in the produced executable —
        // neither as ASCII nor as UTF-16 (this is the defect the AES layer fixes).
        var haystack = File.ReadAllBytes(output);
        Assert.That(IndexOf(haystack, Encoding.UTF8.GetBytes(script)), Is.EqualTo(-1),
            "the raw batch source must not appear as ASCII in the executable");
        Assert.That(IndexOf(haystack, Encoding.Unicode.GetBytes(script)), Is.EqualTo(-1),
            "the raw batch source must not appear as UTF-16 in the executable");

        // An attacker extracts the UTF-16 Base64 blob and gunzips it: with AES
        // that must fail — gzip sees random bytes and rejects them.
        var blob = ExtractUtf16Base64Blob(haystack, minChars: 40);
        Assert.That(blob, Is.Not.Null, "the Base64 payload must still be embedded as a UTF-16 literal");
        var blobBytes = Convert.FromBase64String(blob!);
        Assert.Throws<InvalidDataException>(() => Gunzip(blobBytes),
            "the extracted payload must not decompress back to the batch source");
    }

    [Test]
    public async Task Convert_RequireAdministrator_EmbedsAdminManifest()
    {
        RequireCsc();

        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\necho elevated\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            ExecutionLevel = BatExecutionLevel.RequireAdministrator,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
        Assert.That(ManifestVerifier.DetectLevel(output), Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
    }

    [Test]
    public async Task Convert_NoConsole_ProducesGuiSubsystemExecutable()
    {
        RequireCsc();

        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\necho hidden\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            NoConsole = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Pe!.IsDotNet, Is.True);
        Assert.That(ReadSubsystem(output), Is.EqualTo(2), "no-console mode must emit a Windows GUI subsystem exe");
        Assert.That(result.Warnings, Has.Some.Contains("no-console mode"));
    }

    [Test]
    public async Task Convert_WithPassphrase_ObfuscatesAndWarnsHonestly()
    {
        RequireCsc();

        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\necho secret-ish\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            Passphrase = "correct horse battery",
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.PayloadObfuscated, Is.True);
        Assert.That(result.PayloadEncrypted, Is.True, "XOR passphrase stacks on top of the AES layer");
        Assert.That(result.Warnings, Has.Some.Contains("XOR"));
        Assert.That(result.Warnings, Has.Some.Contains("not copy protection"));
    }

    [Test]
    public async Task Convert_EncryptPayloadDisabled_LeavesRecoverablePayload()
    {
        RequireCsc();

        var input = NewInputPath();
        const string script = "@echo off\r\necho plaintext payload opt-out\r\n";
        File.WriteAllText(input, script);
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EncryptPayload = false,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.PayloadEncrypted, Is.False);
        Assert.That(result.PayloadCompressed, Is.True);
        Assert.That(result.Warnings, Is.Empty);

        // With encryption off the old behaviour is preserved: the Base64+GZip payload
        // is embedded verbatim and can be recovered from the executable.
        var expectedPayload = BatPayloadCodec.Encode(
            Encoding.UTF8.GetBytes(script), compress: true, passphrase: null, encrypt: false);
        var haystack = File.ReadAllBytes(output);
        var needle = Encoding.Unicode.GetBytes(expectedPayload.Data);
        Assert.That(IndexOf(haystack, needle), Is.GreaterThanOrEqualTo(0),
            "without encryption the Base64 payload must appear verbatim as a UTF-16 literal");
    }

    [Test]
    public async Task Convert_ExistingOutputWithoutOverwrite_FailsAndKeepsFile()
    {
        RequireCsc();

        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\n");
        var output = NewOutputPath();
        File.WriteAllText(output, "precious existing bytes");

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = false,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.OutputFileExists));
        Assert.That(File.ReadAllText(output), Is.EqualTo("precious existing bytes"));
    }

    [Test]
    public async Task Convert_PowerShellInput_IsRejectedAsNotAScript()
    {
        var input = Path.Combine(Path.GetTempPath(), $"props1-notbat-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(input, "Write-Host 'not a batch file'");
        var output = NewOutputPath();

        try
        {
            var result = await CreateEngine().ConvertAsync(new BatConversionRequest
            {
                InputPath = input,
                OutputPath = output,
            });

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotScript));
            Assert.That(File.Exists(output), Is.False);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Test]
    public async Task Convert_SecretFinding_NonFatal_WarnsAndStillSucceeds()
    {
        RequireCsc();

        var input = NewInputPath();
        File.WriteAllText(input,
            "@echo off\r\nset KEY=AKIAIOSFODNN7EXAMPLE\r\necho %KEY%\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EnableSecretScan = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.SecretFindings, Is.Not.Empty, "the AWS key must be detected");
        Assert.That(result.Warnings, Has.Some.Contains("secret"));
        Assert.That(result.SecretFindings[0].MaskedValue, Does.Not.Contain("IOSFODNN7EXAMPLE"));
        Assert.That(File.Exists(output), Is.True);
    }

    [Test]
    public async Task Convert_SecretFindingFatal_StopsBeforeCompilation()
    {
        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\nset KEY=AKIAIOSFODNN7EXAMPLE\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EnableSecretScan = true,
            SecretFindingsAreFatal = true,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.SecretDetected));
        Assert.That(result.SecretFindings, Is.Not.Empty);
        Assert.That(File.Exists(output), Is.False, "nothing must be compiled when findings are fatal");
    }

    [Test]
    public async Task Convert_WithRepoIcon_ProducesExecutable()
    {
        RequireCsc();

        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Assert.Ignore("repository root (ProPS1Studio.sln) not found from the test output directory.");
            return;
        }

        var icon = Path.Combine(repoRoot, "src", "ProPS1Studio.App", "Assets", "props1.ico");
        if (!File.Exists(icon))
        {
            Assert.Ignore($"repo icon not found: {icon}");
            return;
        }

        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\necho icon test\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            IconPath = icon,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Pe!.IsValid, Is.True, result.Pe.Error);
        Assert.That(result.Pe.IsDotNet, Is.True);
    }

    [Test]
    public async Task Convert_ExtraFile_BumpsCountAndStillSucceeds()
    {
        RequireCsc();

        var extra = Path.Combine(_root, "input", $"extra-{Guid.NewGuid():N}.txt");
        File.WriteAllText(extra, "bundled payload bytes");
        var input = NewInputPath();
        File.WriteAllText(input, "@echo off\r\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            ExtraFilePaths = new[] { extra },
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.ExtraFileCount, Is.EqualTo(1));
        Assert.That(result.Pe!.IsDotNet, Is.True);
    }

    [Test]
    public async Task Convert_MissingInput_FailsWithInputNotFound()
    {
        var result = await CreateEngine().ConvertAsync(new BatConversionRequest
        {
            InputPath = Path.Combine(_root, "input", $"missing-{Guid.NewGuid():N}.bat"),
            OutputPath = NewOutputPath(),
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public async Task Convert_InvalidConsoleColor_FailsWithInvalidOption()
    {
        var input = WriteBatch("@echo off\r\n");
        var output = NewOutputPath();

        try
        {
            var result = await CreateEngine().ConvertAsync(new BatConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                ConsoleColorHex = "XYZ",
            });

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InvalidOption));
            Assert.That(File.Exists(output), Is.False);
        }
        finally
        {
            File.Delete(input);
        }
    }
}
