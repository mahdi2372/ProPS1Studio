using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;

namespace ProPS1Studio.Core.Tests;

/// <summary>Unit tests for the end-to-end .ps1 conversion pipeline using a fake process runner
/// and a fake capability probe, so no ps2exe/PowerShell is required.</summary>
[TestFixture]
public class ConversionEngineTests
{
    private readonly List<string> _tempFiles = new();

    [TearDown]
    public void Cleanup()
    {
        foreach (var file in _tempFiles)
        {
            TryDelete(file);
        }

        _tempFiles.Clear();
    }

    private static Ps2exeCapability FullCapability() => new()
    {
        IsAvailable = true,
        ModuleVersion = "1.0.18",
        ModulePath = "/modules/ps2exe",
        Parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inputFile", "outputFile", "requireAdmin", "noConsole", "iconFile",
            "title", "description", "company", "product", "copyright", "version",
        },
    };

    private sealed class StaticProbe : IPs2exeCapabilityProbe
    {
        private readonly Ps2exeCapability _capability;

        public StaticProbe(Ps2exeCapability capability) => _capability = capability;

        public Task<Ps2exeCapability> ProbeAsync(string? modulePath = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_capability);

        public void Invalidate()
        {
        }
    }

    private string NewInput(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1-conv-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content, new UTF8Encoding(true));
        _tempFiles.Add(path);
        return path;
    }

    private string NewOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1-conv-{Guid.NewGuid():N}.exe");
        _tempFiles.Add(path);
        return path;
    }

    private static ConversionEngine CreateEngine(FakeProcessRunner runner, Ps2exeCapability capability) =>
        new(runner, new NullLogger(), new StaticProbe(capability));

    [Test]
    public async Task ConvertAsync_SimpleScript_ProducesValidOutput()
    {
        var runner = new FakeProcessRunner().ProducesPeFromScript();
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();

        var result = await CreateEngine(runner, FullCapability())
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = true,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(File.Exists(output), Is.True);
        Assert.That(result.OutputSizeBytes, Is.GreaterThan(0));
    }

    [Test]
    public async Task ConvertAsync_UnsupportedIcon_IsDroppedAndWarned()
    {
        var capability = FullCapability();
        capability.Parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inputFile", "outputFile", "requireAdmin", "noConsole",
        }; // no iconFile

        var runner = new FakeProcessRunner().ProducesPeFromScript();
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();
        var iconPath = Path.Combine(Path.GetTempPath(), $"props1-icon-{Guid.NewGuid():N}.ico");
        File.WriteAllBytes(iconPath, new byte[] { 0, 0, 1, 0 });
        _tempFiles.Add(iconPath);

        var result = await CreateEngine(runner, capability)
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = true,
                IconPath = iconPath,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Warnings, Has.Some.Contains("default icon is used"));

        // The generated wrapper must NOT request the unsupported iconFile parameter.
        Assert.That(runner.ScriptTexts, Has.Count.GreaterThan(0));
        Assert.That(runner.ScriptTexts[0], Does.Not.Contain("iconFile"));
    }

    [Test]
    public async Task ConvertAsync_FatalSecret_ReturnsSecretDetectedError()
    {
        var runner = new FakeProcessRunner().ProducesPeFromScript();
        var input = NewInput("$token = 'ghp_" + new string('A', 40) + "'");
        var output = NewOutput();

        var result = await CreateEngine(runner, FullCapability())
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = true,
                SecretFindingsAreFatal = true,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.SecretDetected));
        Assert.That(result.SecretFindings, Is.Not.Empty);
    }

    [Test]
    public async Task ConvertAsync_OutputExistsWithoutOverwrite_Fails()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();
        File.WriteAllBytes(output, new byte[] { 1, 2, 3 });

        var result = await CreateEngine(runner, FullCapability())
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = false,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.OutputFileExists));
    }

    [Test]
    public async Task ConvertAsync_ProbeUnavailable_FailsWithPs2exeMissing()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();

        var result = await CreateEngine(runner, Ps2exeCapability.Unavailable("not installed"))
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = true,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.Ps2exeMissing));
    }

    [Test]
    public async Task ConvertAsync_CompilerExitsNonZero_FailsWithCompileFailed()
    {
        var runner = new FakeProcessRunner();
        runner.Handler = _ => new ProcessResult { ExitCode = 1, Duration = TimeSpan.Zero, StdErr = "boom" };
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();

        var result = await CreateEngine(runner, FullCapability())
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = true,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.CompileFailed));
    }

    [Test]
    public async Task ConvertAsync_CancelledToken_PropagatesCancellation()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CreateEngine(runner, FullCapability()).ConvertAsync(
                new ConversionRequest
                {
                    InputPath = input,
                    OutputPath = output,
                    OverwriteOutput = true,
                    TimeoutSeconds = 30,
                },
                cts.Token));
    }

    [Test]
    public async Task ConvertAsync_UpxMissing_WarnsAndSucceeds()
    {
        var runner = new FakeProcessRunner().ProducesPeFromScript();
        var input = NewInput("Write-Host 'hello'");
        var output = NewOutput();

        var result = await CreateEngine(runner, FullCapability())
            .ConvertAsync(new ConversionRequest
            {
                InputPath = input,
                OutputPath = output,
                OverwriteOutput = true,
                CompressWithUpx = true,
                TimeoutSeconds = 30,
            });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.UpxCompressed, Is.False);
        Assert.That(result.Warnings, Has.Some.Contains("UPX is not available."));
    }

    private static void TryDelete(string path)
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
            // ignored
        }
    }
}
