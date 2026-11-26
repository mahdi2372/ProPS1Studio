using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;

namespace ProPS1Studio.Core.Tests;

/// <summary>Unit tests for the batch-to-executable pipeline using a fake process runner and a
/// fabricated PE compiler output, so no csc.exe / PowerShell / ps2exe is required.</summary>
[TestFixture]
public class BatToExeEngineTests
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

    private string NewInput(string content, string extension = ".bat")
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1-bat-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content, new UTF8Encoding(true));
        _tempFiles.Add(path);
        return path;
    }

    private string NewOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1-bat-{Guid.NewGuid():N}.exe");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>A stand-in csc.exe path: it must exist on disk for BatCscLocator, but the fake
    /// runner never actually executes it.</summary>
    private string FakeCsc()
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1-csc-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x45, 0x00, 0x00 });
        _tempFiles.Add(path);
        return path;
    }

    private BatToExeEngine CreateEngine(FakeProcessRunner runner, IAuthenticodeSigner? signer = null) =>
        new(runner, new NullLogger(), signer, cscPathOverride: FakeCsc());

    [Test]
    public async Task ConvertAsync_SimpleBatch_ProducesExecutable()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc();
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(File.Exists(output), Is.True);
        Assert.That(result.OutputSizeBytes, Is.GreaterThan(0));
        Assert.That(result.Pe?.IsValid, Is.True);
        Assert.That(result.Pe?.IsDotNet, Is.True);
    }

    [Test]
    public async Task ConvertAsync_RequireAdmin_ManifestMatchesRequest()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc(level: "requireAdministrator");
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            ExecutionLevel = BatExecutionLevel.RequireAdministrator,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
    }

    [Test]
    public async Task ConvertAsync_RequireAdminButAsInvokerManifest_Fails()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc(level: "asInvoker");
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            ExecutionLevel = BatExecutionLevel.RequireAdministrator,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.AdminManifestMissing));
    }

    [Test]
    public async Task ConvertAsync_SecretInBatch_IsMaskedAndReported()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc();
        var input = NewInput("@set TOKEN=ghp_" + new string('A', 40) + "\r\n");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
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
        Assert.That(result.SecretFindings!.All(f => !f.MaskedValue.Contains(new string('A', 40))), Is.True);
    }

    [Test]
    public async Task ConvertAsync_NonFatalSecret_SucceedsWithWarning()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc();
        var input = NewInput("@set TOKEN=ghp_" + new string('A', 40) + "\r\n");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            SecretFindingsAreFatal = false,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.SecretFindings, Is.Not.Empty);
        Assert.That(result.Warnings, Has.Some.Contains("secret"));
    }

    [Test]
    public async Task ConvertAsync_MissingOutputDirectory_Fails()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc();
        var input = NewInput("@echo hello\r\n");
        var output = Path.Combine(
            Path.GetTempPath(),
            "props1-missing-" + Guid.NewGuid().ToString("N"),
            "deep",
            "out.exe");

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task ConvertAsync_OutputExistsWithoutOverwrite_Fails()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();
        File.WriteAllBytes(output, new byte[] { 1, 2, 3 });

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
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
    public async Task ConvertAsync_NonBatchExtension_FailsValidation()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("not really a batch file with no commands??\r\n", extension: ".txt");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.InputNotScript));
        Assert.That(runner.Calls, Is.Empty); // validation must fail before any compiler is invoked
    }

    [Test]
    public async Task ConvertAsync_CompilerExitsNonZero_FailsWithCompileFailed()
    {
        var runner = new FakeProcessRunner();
        runner.Handler = _ => new ProcessResult { ExitCode = 1, Duration = TimeSpan.Zero, StdErr = "CS0246: type not found" };
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var result = await CreateEngine(runner).ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.CscFailed));
        Assert.That(result.CompilerOutput, Has.Some.Contains("CS0246"));
    }

    [Test]
    public async Task ConvertAsync_CscMissing_Fails()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var engine = new BatToExeEngine(runner, new NullLogger(), cscPathOverride: "/nonexistent/csc.exe");
        var result = await engine.ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.Code, Is.EqualTo(ErrorCode.CscMissing));
    }

    [Test]
    public async Task ConvertAsync_SigningFailure_DeletesOutputByDefault()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc();
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var engine = CreateEngine(runner, new FakeSigner { Fails = true });
        var result = await engine.ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            SignAfterConversion = true,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(File.Exists(output), Is.False); // the unsigned binary must not survive
    }

    [Test]
    public async Task ConvertAsync_SigningFailureNonFatal_KeepsOutput()
    {
        var runner = new FakeProcessRunner().ProducesPeFromCsc();
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        var engine = CreateEngine(runner, new FakeSigner { Fails = true });
        var result = await engine.ConvertAsync(new BatConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
            SignAfterConversion = true,
            SigningFailureIsFatal = false,
            TimeoutSeconds = 30,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(File.Exists(output), Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("signing failed"));
        Assert.That(result.Signed, Is.False);
    }

    [Test]
    public async Task ConvertAsync_CancelledToken_PropagatesCancellation()
    {
        var runner = new FakeProcessRunner();
        var input = NewInput("@echo hello\r\n");
        var output = NewOutput();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CreateEngine(runner).ConvertAsync(
                new BatConversionRequest
                {
                    InputPath = input,
                    OutputPath = output,
                    OverwriteOutput = true,
                    TimeoutSeconds = 30,
                },
                cts.Token));
    }

    private sealed class FakeSigner : IAuthenticodeSigner
    {
        public bool Fails { get; set; }

        public Task<SignResult> SignAsync(SignRequest request, CancellationToken cancellationToken = default)
        {
            var outcome = Fails
                ? new SignResult
                {
                    Success = false,
                    Error = new AppError(ErrorCode.SignFailed, "signing failed: no certificate configured", "Configure a certificate."),
                }
                : new SignResult
                {
                    Success = true,
                    SignatureStatus = "signed",
                };
            return Task.FromResult(outcome);
        }
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
