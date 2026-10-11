using System.IO.Compression;
using NUnit.Framework;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Diagnostics;
using ProPS1Studio.Core.Env;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Security;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Setup;
using ProPS1Studio.Core.Upx;

namespace ProPS1Studio.Integration.Tests;

/// <summary>
/// Real end-to-end runs against the tools actually installed on this machine
/// (ps2exe module, child PowerShell, real PE output). Nothing here executes the
/// converted script. Missing prerequisites skip instead of failing.
/// </summary>
[TestFixture]
public class EndToEndPipelineTests
{
    private string _root = null!;
    private AppLogger _logger = null!;
    private ProcessRunner _runner = null!;
    private Ps2exeCapabilityProbe _probe = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"props1-it-{Guid.NewGuid():N}");
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

    private ConversionEngine CreateEngine(IUpxPacker? upx = null) =>
        new(_runner, _logger, _probe, upx, signer: null, scanner: new SecretScanner());

    private string NewInputPath() => Path.Combine(_root, "input", $"in-{Guid.NewGuid():N}.ps1");

    private string NewOutputPath() => Path.Combine(_root, "output", $"out-{Guid.NewGuid():N}.exe");

    private async Task<Ps2exeCapability> RequirePs2exeAsync()
    {
        var capability = await _probe.ProbeAsync();
        if (!capability.IsAvailable)
        {
            Assert.Ignore($"ps2exe is not installed: {capability.Error}");
        }

        return capability;
    }

    [Test]
    public async Task CapabilityProbe_FindsInstalledPs2exeWithRealParameters()
    {
        var capability = await RequirePs2exeAsync();

        Assert.That(capability.ModuleVersion, Is.Not.Null.And.Not.Empty);
        Assert.That(capability.Supports("requireAdmin"), Is.True, "ps2exe 1.0.18 must expose -requireAdmin");
        Assert.That(capability.Supports("noConsole"), Is.True, "ps2exe 1.0.18 must expose -noConsole");
        Assert.That(capability.Supports("outputFile"), Is.True);
        Assert.That(capability.Supports("inputFile"), Is.True);
    }

    [Test]
    public async Task Convert_SimpleConsoleScript_ProducesValidPeExecutable()
    {
        await RequirePs2exeAsync();

        var input = NewInputPath();
        File.WriteAllText(input, "Write-Host 'hello from ProPS1 Studio'\nGet-Date\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(File.Exists(output), Is.True, "output executable was produced");
        Assert.That(result.OutputSizeBytes, Is.GreaterThan(1024));
        Assert.That(result.Pe!.IsValid, Is.True, result.Pe.Error);
        Assert.That(result.ExecutionLevel, Is.Not.EqualTo(RequestedExecutionLevel.RequireAdministrator));
        Assert.That(result.UpxCompressed, Is.False);
        Assert.That(result.Signed, Is.False);
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(File.Exists(input), Is.True, "the source script is never consumed");
    }

    [Test]
    public async Task Convert_RequireAdministrator_EmbedsAdminManifest()
    {
        await RequirePs2exeAsync();

        var input = NewInputPath();
        File.WriteAllText(input, "Write-Host 'elevated'\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            RequireAdministrator = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.ExecutionLevel, Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
        Assert.That(ManifestVerifier.RequestsAdministrator(output), Is.True);
    }

    [Test]
    public async Task Convert_NoConsole_ProducesGuiExecutable()
    {
        await RequirePs2exeAsync();

        var input = NewInputPath();
        File.WriteAllText(input, "$null = [System.Windows.Forms.MessageBox]::Show('hi')\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            NoConsole = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Pe!.IsValid, Is.True);
        Assert.That(result.OutputSizeBytes, Is.GreaterThan(1024));
    }

    [Test]
    public async Task Convert_CompressWithUpx_LeavesDotNetExeUncompressedWithWarning()
    {
        await RequirePs2exeAsync();

        var input = NewInputPath();
        File.WriteAllText(input, "Write-Host 'pack me?'\n");
        var output = NewOutputPath();

        var result = await CreateEngine(new UpxManager(_runner, _logger)).ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            CompressWithUpx = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.UpxCompressed, Is.False, "ps2exe output is .NET, UPX must skip it");
        Assert.That(result.Warnings, Has.Some.Contains("UPX"));
    }

    [Test]
    public async Task Convert_ExistingOutputWithoutOverwrite_FailsAndKeepsFile()
    {
        var input = NewInputPath();
        File.WriteAllText(input, "Write-Host 1\n");
        var output = NewOutputPath();
        File.WriteAllText(output, "precious existing bytes");

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
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
    public async Task Convert_MissingInput_FailsWithInputNotFound()
    {
        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = Path.Combine(_root, "input", $"missing-{Guid.NewGuid():N}.ps1"),
            OutputPath = NewOutputPath(),
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public async Task Convert_SecretFindingsFatal_StopsBeforeCompilation()
    {
        var input = NewInputPath();
        File.WriteAllText(input, "$key = 'AKIAIOSFODNN7EXAMPLE'\nWrite-Host $key\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            SecretFindingsAreFatal = true,
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.SecretFindings, Is.Not.Empty, "the AWS key must be detected");
        Assert.That(result.SecretFindings[0].MaskedValue, Does.Not.Contain("IOSFODNN7EXAMPLE"));
        Assert.That(File.Exists(output), Is.False, "nothing must be compiled when findings are fatal");
    }

    [Test]
    public async Task Convert_WithSecrets_NonFatal_WarnsAndStillSucceeds()
    {
        await RequirePs2exeAsync();

        var input = NewInputPath();
        File.WriteAllText(input, "token = 'ghp_abcdefghijklmnopqrstuvwxyz0123456789'\nWrite-Host ok\n");
        var output = NewOutputPath();

        var result = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            EnableSecretScan = true,
        });

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.SecretFindings, Is.Not.Empty);
        Assert.That(result.Warnings, Has.Some.Contains("secret"));
        Assert.That(File.Exists(output), Is.True);
    }

    [Test]
    public async Task Convert_Overwrite_ReplacesPreviousOutput()
    {
        await RequirePs2exeAsync();

        var input = NewInputPath();
        File.WriteAllText(input, "Write-Host 'v1'\n");
        var output = NewOutputPath();

        var first = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
        });
        Assert.That(first.Success, Is.True, first.Error?.ToString());
        var firstBytes = File.ReadAllBytes(output);

        File.WriteAllText(input, "Write-Host 'v2'\nGet-Process | Out-Null\n");
        var second = await CreateEngine().ConvertAsync(new ConversionRequest
        {
            InputPath = input,
            OutputPath = output,
            OverwriteOutput = true,
        });

        Assert.That(second.Success, Is.True, second.Error?.ToString());
        Assert.That(File.ReadAllBytes(output), Is.Not.EqualTo(firstBytes), "output was regenerated");
    }

    [Test]
    public async Task EnvironmentProbe_ReportsRealMachineState()
    {
        var probe = new EnvironmentProbe(_runner, _probe, new UpxManager(_runner, _logger));
        var report = await probe.ProbeAsync();

        Assert.That(report.OsVersion, Is.Not.Null.And.Not.Empty);
        Assert.That(report.MachineName, Is.Not.Null.And.Not.Empty);
        Assert.That(report.DotNetRuntime, Is.Not.Null.And.Not.Empty);
        Assert.That(report.PowerShellVersion, Is.Not.Null.And.Not.Empty);
        Assert.That(report.TotalDiskBytes, Is.GreaterThan(0));
        Assert.That(report.GeneratedAt, Is.EqualTo(DateTime.Now).Within(TimeSpan.FromMinutes(5)));
    }

    [Test]
    public async Task DependencyManager_ReportsPs2exeInstalledOnThisMachine()
    {
        var upx = new UpxManager(_runner, _logger);
        var manager = new DependencyManager(
            _probe,
            new Ps2exeInstaller(_runner, _logger, _probe),
            new UpxInstaller(_logger, upx),
            upx);

        var status = await manager.GetStatusAsync();

        Assert.That(status, Has.Count.EqualTo(2));
        var ps2exe = status.Single(s => s.Name == "ps2exe");
        Assert.That(ps2exe.Required, Is.True);
        Assert.That(ps2exe.Installed, Is.True, ps2exe.Details);
        Assert.That(ps2exe.Version, Is.Not.Null.And.Not.Empty);

        var upxStatus = status.Single(s => s.Name == "UPX");
        Assert.That(upxStatus.Required, Is.False, "UPX is optional");
    }

    [Test]
    public async Task UpxManager_AvailabilityCheck_ReturnsRealResult()
    {
        var availability = await new UpxManager(_runner, _logger).CheckAvailabilityAsync();

        Assert.That(availability, Is.Not.Null);
        if (availability.Available)
        {
            Assert.That(availability.Version, Does.Match(@"^\d+\.\d+"), "expected a bare version like 5.2.1");
            Assert.That(File.Exists(availability.Path!), Is.True);
        }
    }

    [Test]
    public void DiagnosticExporter_CreatesRedactedArchive()
    {
        var settingsStore = new SettingsStore(Path.Combine(_root, "settings.json"), _logger);
        settingsStore.Save(new AppSettings
        {
            RecentConversions =
            {
                new RecentConversion
                {
                    Timestamp = DateTime.Now,
                    InputPath = @"C:\in\demo.ps1",
                    OutputPath = @"C:\out\demo.exe",
                    Success = true,
                },
            },
        });
        _logger.Write(LogLevel.Info, "test", "export marker line");

        var exporter = new DiagnosticExporter(_logger, settingsStore);
        var zipPath = exporter.Export(null);

        Assert.That(File.Exists(zipPath), Is.True);
        Assert.That(zipPath, Does.EndWith(".zip"));

        string[] names;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            names = archive.Entries.Select(e => e.FullName).ToArray();
        }

        Assert.That(names, Does.Contain("readme.txt"));
        Assert.That(names, Does.Contain("log.txt"));
        Assert.That(names, Does.Contain("settings.json"));
        Assert.That(names, Does.Contain("environment.txt"));

        File.Delete(zipPath);
    }
}
