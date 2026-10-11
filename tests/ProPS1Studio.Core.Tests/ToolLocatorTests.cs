using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Processes;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class ToolLocatorTests
{
    private readonly List<string> _tempFiles = new();

    [TearDown]
    public void Cleanup()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }

        _tempFiles.Clear();
    }

    [Test]
    public void ComputeSha256_ReturnsKnownHashForKnownContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1hash-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));
        _tempFiles.Add(path);

        var hash = ToolLocator.ComputeSha256(path);
        Assert.That(hash, Is.EqualTo("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
    }

    [Test]
    public void ComputeSha256_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ToolLocator.ComputeSha256(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.bin")));
    }

    [Test]
    public void MatchesAnySha256_IsCaseInsensitive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1hash-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));
        _tempFiles.Add(path);

        Assert.That(ToolLocator.MatchesAnySha256(path, new[] { "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" }), Is.True);
        Assert.That(ToolLocator.MatchesAnySha256(path, new[] { "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" }), Is.True);
        Assert.That(ToolLocator.MatchesAnySha256(path, new[] { "0000000000000000000000000000000000000000000000000000000000000000" }), Is.False);
        Assert.That(ToolLocator.MatchesAnySha256(path, Array.Empty<string>()), Is.False);
    }

    [Test]
    public void MatchesAnySha256_HandlesPrefixesAndWhitespace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1hash-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));
        _tempFiles.Add(path);

        Assert.That(
            ToolLocator.MatchesAnySha256(path, new[] { " sha256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD " }),
            Is.True);
    }

    [Test]
    public void FindOnPath_FindsSystemExecutable()
    {
        var found = ToolLocator.FindOnPath("cmd.exe");
        Assert.That(found, Is.Not.Null);
        Assert.That(File.Exists(found), Is.True);
    }

    [Test]
    public void FindOnPath_ReturnsNullForMissingExecutable()
    {
        Assert.That(ToolLocator.FindOnPath($"definitely-missing-{Guid.NewGuid():N}.exe"), Is.Null);
    }
}

[TestFixture]
public class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new();

    [Test]
    public async Task RunAsync_CapturesStdoutAndExitCode()
    {
        var result = await _runner.RunAsync(new ProcessSpec
        {
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "echo hello-props1" },
            TimeoutMilliseconds = 30_000,
        });

        Assert.That(result.FailedToStart, Is.False, result.StartError);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.Success, Is.True);
        Assert.That(result.StdOut, Does.Contain("hello-props1"));
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    public async Task RunAsync_NonZeroExit_IsNotSuccess()
    {
        var result = await _runner.RunAsync(new ProcessSpec
        {
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "exit 7" },
            TimeoutMilliseconds = 30_000,
        });

        Assert.That(result.ExitCode, Is.EqualTo(7));
        Assert.That(result.Success, Is.False);
        Assert.That(result.TimedOut, Is.False);
    }

    [Test]
    public async Task RunAsync_CapturesStderr()
    {
        var result = await _runner.RunAsync(new ProcessSpec
        {
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "echo error-line 1>&2" },
            TimeoutMilliseconds = 30_000,
        });

        Assert.That(result.StdErr, Does.Contain("error-line"));
    }

    [Test]
    public async Task RunAsync_Timeout_KillsAndReportsTimedOut()
    {
        var result = await _runner.RunAsync(new ProcessSpec
        {
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "ping -n 30 127.0.0.1 > nul" },
            TimeoutMilliseconds = 1_000,
        });

        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task RunAsync_MissingExecutable_ReportsFailedToStart()
    {
        var result = await _runner.RunAsync(new ProcessSpec
        {
            FileName = $"missing-{Guid.NewGuid():N}.exe",
            TimeoutMilliseconds = 5_000,
        });

        Assert.That(result.FailedToStart, Is.True);
        Assert.That(result.StartError, Is.Not.Null.And.Not.Empty);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task RunAsync_StreamsOutputToCallback()
    {
        var lines = new List<string>();
        await _runner.RunAsync(
            new ProcessSpec
            {
                FileName = "cmd.exe",
                Arguments = new[] { "/c", "echo streamed-line" },
                TimeoutMilliseconds = 30_000,
            },
            line => { lock (lines) lines.Add(line); });

        lock (lines)
        {
            Assert.That(lines, Has.Some.Contains("streamed-line"));
        }
    }
}
