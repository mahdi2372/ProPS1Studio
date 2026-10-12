using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Protection;

namespace ProPS1Studio.Core.Tests;

/// <summary>
/// Crypto and compile tests for ExeProtector. The stub is compiled with the
/// .NET Framework csc.exe but never executed - runtime behavior is verified
/// separately with explicit permission.
/// </summary>
[TestFixture]
public class ExeProtectorTests
{
    private const string Marker = "PROPS1-PROTECT-CORE-MARKER-77x";

    [Test]
    public void Protect_ThenDecrypt_RoundtripsInnerExe()
    {
        var inner = Encoding.UTF8.GetBytes(new string('A', 12345) + Marker);

        var payload = ExeProtector.Protect(inner);

        Assert.That(ExeProtector.Decrypt(payload.Blob, payload.Key), Is.EqualTo(inner));
    }

    [Test]
    public void Protect_BlobDoesNotContainInnerBytes()
    {
        var marker = Encoding.UTF8.GetBytes(Marker);
        var inner = new byte[marker.Length + 8192];
        marker.CopyTo(inner, 0);

        var payload = ExeProtector.Protect(inner);

        Assert.That(Contains(payload.Blob, marker), Is.False,
            "the encrypted blob must not contain the inner executable as plaintext");
        Assert.That(payload.Blob.Length, Is.GreaterThan(inner.Length),
            "the blob carries the IV plus PKCS7 padding");
    }

    [Test]
    public void Protect_UsesFreshRandomKeyAndBlobPerBuild()
    {
        var inner = Encoding.UTF8.GetBytes("same inner content");

        var first = ExeProtector.Protect(inner);
        var second = ExeProtector.Protect(inner);

        Assert.That(second.Key, Is.Not.EqualTo(first.Key), "every build gets a fresh AES key");
        Assert.That(second.Blob, Is.Not.EqualTo(first.Blob), "the same inner bytes encrypt differently");
        Assert.That(first.Key.Length, Is.EqualTo(32), "AES-256 key");
        Assert.That(second.StubSource, Is.Not.EqualTo(first.StubSource), "embedded key material is per-build");
    }

    [Test]
    public void Protect_EmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => ExeProtector.Protect(Array.Empty<byte>()));
    }

    [Test]
    public void StubSource_ContainsAntiDebugAndDecryption()
    {
        var payload = ExeProtector.Protect(Encoding.UTF8.GetBytes("inner"));

        Assert.Multiple(() =>
        {
            Assert.That(payload.StubSource, Does.Contain("Debugger.IsAttached"));
            Assert.That(payload.StubSource, Does.Contain("IsDebuggerPresent"));
            Assert.That(payload.StubSource, Does.Contain("Assembly.Load"));
            Assert.That(payload.StubSource, Does.Contain("CreateDecryptor"));
            Assert.That(payload.StubSource, Does.Contain(ExeProtector.ResourceName));
        });
    }

    [Test]
    public async Task BuildProtectedExeAsync_ProducesDotNetStubWithoutPlaintext()
    {
        var csc = RequireCsc();
        var root = NewRoot();
        try
        {
            var innerPath = Path.Combine(root, "inner.exe");
            var marker = Encoding.UTF8.GetBytes(Marker);
            var innerBytes = new byte[marker.Length + 4096];
            marker.CopyTo(innerBytes, 0);
            await File.WriteAllBytesAsync(innerPath, innerBytes);

            var manifestPath = Path.Combine(root, "stub.manifest");
            await File.WriteAllTextAsync(manifestPath,
                BatManifestBuilder.Build(BatExecutionLevel.AsInvoker));

            var stubPath = Path.Combine(root, "stub.exe");
            var compilerOutput = new List<string>();
            using var logger = new AppLogger(Path.Combine(root, "logs"));
            var runner = new ProcessRunner();

            var error = await ExeProtector.BuildProtectedExeAsync(
                runner, csc, root, innerPath, stubPath, manifestPath,
                iconFullPath: null, noConsole: true, timeoutSeconds: 60,
                compilerOutput, logger, CancellationToken.None);

            Assert.That(error, Is.Null, error?.Message + Environment.NewLine +
                string.Join(Environment.NewLine, compilerOutput));
            Assert.That(File.Exists(stubPath), Is.True, "the stub executable must be produced");

            var pe = PeFile.Read(stubPath);
            Assert.That(pe.IsValid, Is.True, pe.Error);
            Assert.That(pe.IsDotNet, Is.True, "the stub is a .NET Framework assembly");
            Assert.That(ManifestVerifier.DetectLevel(stubPath),
                Is.EqualTo(RequestedExecutionLevel.AsInvoker),
                "the stub carries the requested asInvoker manifest");

            var stubBytes = File.ReadAllBytes(stubPath);
            Assert.That(Contains(stubBytes, marker), Is.False,
                "the embedded inner executable must be encrypted, never plaintext");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task BuildProtectedExeAsync_UnknownCscPath_ReportsCscMissing()
    {
        var root = NewRoot();
        try
        {
            var innerPath = Path.Combine(root, "inner.exe");
            await File.WriteAllBytesAsync(innerPath, Encoding.UTF8.GetBytes("inner"));
            var manifestPath = Path.Combine(root, "stub.manifest");
            await File.WriteAllTextAsync(manifestPath,
                BatManifestBuilder.Build(BatExecutionLevel.AsInvoker));
            using var logger = new AppLogger(Path.Combine(root, "logs"));

            var error = await ExeProtector.BuildProtectedExeAsync(
                new ProcessRunner(),
                Path.Combine(root, "no-such-csc.exe"),
                root, innerPath, Path.Combine(root, "stub.exe"), manifestPath,
                iconFullPath: null, noConsole: true, timeoutSeconds: 15,
                new List<string>(), logger, CancellationToken.None);

            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Code, Is.EqualTo(ErrorCode.CscMissing));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string RequireCsc()
    {
        var csc = BatCscLocator.Find();
        if (csc is null)
        {
            Assert.Ignore("csc.exe (the .NET Framework C# compiler) is not available on this machine.");
        }

        return csc!;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"props1-protect-core-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
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
}
