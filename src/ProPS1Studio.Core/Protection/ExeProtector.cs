using System.Security.Cryptography;
using System.Text;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;

namespace ProPS1Studio.Core.Protection;

/// <summary>
/// Wraps a finished executable in a per-build AES-256-CBC encrypted stub:
/// the real program is stored as an embedded resource and decrypted at launch.
/// A static anti-debug gate refuses to run under a debugger or with common
/// reverse-engineering tools present. Honest scope: this is obfuscation that
/// forces decompilation or a memory dump to inspect - not DRM or copy
/// protection; the decryption key ships inside the executable.
/// </summary>
public static class ExeProtector
{
    /// <summary>Resource name under which the encrypted payload is embedded in the stub.</summary>
    public const string ResourceName = "P";

    /// <summary>Warning appended by the engines whenever protection is enabled.</summary>
    public const string HonestWarning =
        "Output protection: the real program is AES-256 encrypted inside a per-build stub that " +
        "decrypts it at launch and exits when a debugger or a reverse-engineering tool is " +
        "detected. This forces decompilation or a memory dump to inspect - it is obfuscation, " +
        "not DRM or copy protection: the decryption key ships inside the exe.";

    /// <summary>Process names that make the stub exit without running the payload.</summary>
    private const string ToolList =
        "dnSpy;dnSpy.x64;ILSpy;x64dbg;x32dbg;windbg;windbgx;ollydbg;ida;ida64";

    /// <summary>The pieces an engine needs to build the protected executable on disk.</summary>
    public sealed record ProtectedPayload(byte[] Blob, byte[] Key, string StubSource);

    /// <summary>Encrypts the inner executable and generates the matching stub source (C# 5).</summary>
    public static ProtectedPayload Protect(byte[] innerExe)
    {
        ArgumentNullException.ThrowIfNull(innerExe);
        if (innerExe.Length == 0)
        {
            throw new ArgumentException("The inner executable is empty.", nameof(innerExe));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            using (var transform = aes.CreateEncryptor())
            {
                cipher = transform.TransformFinalBlock(innerExe, 0, innerExe.Length);
            }
        }

        var blob = new byte[16 + cipher.Length];
        Buffer.BlockCopy(iv, 0, blob, 0, 16);
        Buffer.BlockCopy(cipher, 0, blob, 16, cipher.Length);

        var mask = RandomNumberGenerator.GetBytes(32);
        var stubSource = BuildStubSource(
            Convert.ToBase64String(Xor(key, mask)),
            Convert.ToBase64String(mask),
            Convert.ToBase64String(Xor(Encoding.UTF8.GetBytes(ToolList), mask)));

        return new ProtectedPayload(blob, key, stubSource);
    }

    /// <summary>Reverse of the stub's decryption - public for tests and roundtrip verification.</summary>
    public static byte[] Decrypt(byte[] blob, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(key);
        if (blob.Length <= 16)
        {
            throw new ArgumentException("The protected blob is truncated.", nameof(blob));
        }

        var iv = new byte[16];
        Buffer.BlockCopy(blob, 0, iv, 0, 16);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var transform = aes.CreateDecryptor();
        return transform.TransformFinalBlock(blob, 16, blob.Length - 16);
    }

    /// <summary>
    /// Writes the encrypted payload and stub source into <paramref name="workDir"/> and compiles
    /// the stub with the .NET Framework csc.exe (never executes the result). Returns null on
    /// success or a mapped <see cref="AppError"/>; compiler output is appended to
    /// <paramref name="compilerOutput"/>.
    /// </summary>
    public static async Task<AppError?> BuildProtectedExeAsync(
        IProcessRunner runner,
        string cscPath,
        string workDir,
        string innerExePath,
        string outputStubPath,
        string manifestPath,
        string? iconFullPath,
        bool noConsole,
        int timeoutSeconds,
        List<string> compilerOutput,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        const string source = nameof(ExeProtector);

        byte[] inner;
        try
        {
            inner = await File.ReadAllBytesAsync(innerExePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorCatalog.InputUnreadable(innerExePath, ex.Message);
        }

        var payload = Protect(inner);

        string payloadPath;
        string sourcePath;
        try
        {
            Directory.CreateDirectory(workDir);
            payloadPath = Path.Combine(workDir, "payload.bin");
            sourcePath = Path.Combine(workDir, "stub.cs");
            await File.WriteAllBytesAsync(payloadPath, payload.Blob, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(sourcePath, payload.StubSource, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorCatalog.OutputDirectoryNotWritable(workDir, ex.Message);
        }

        var arguments = new List<string>
        {
            "/nologo",
            "/target:" + (noConsole ? "winexe" : "exe"),
            "/optimize+",
            "/platform:anycpu",
            "/r:System.dll",
            "/r:System.Core.dll",
            "/r:System.Security.dll",
            "/out:" + outputStubPath,
            "/win32manifest:" + manifestPath,
            "/resource:" + payloadPath + "," + ResourceName,
        };
        if (iconFullPath is not null)
        {
            arguments.Add("/win32icon:" + iconFullPath);
        }

        arguments.Add(sourcePath);

        logger.Write(LogLevel.Info, source, $"Compiling the protection stub with '{cscPath}'.");

        void OnLine(string line)
        {
            compilerOutput.Add(line);
            logger.Write(LogLevel.Info, "csc", line);
        }

        void OnError(string line)
        {
            compilerOutput.Add(line);
            logger.Write(LogLevel.Warn, "csc", line);
        }

        var result = await runner.RunAsync(
            new ProcessSpec
            {
                FileName = cscPath,
                Arguments = arguments,
                WorkingDirectory = workDir,
                TimeoutMilliseconds = Math.Max(15, timeoutSeconds) * 1000,
            },
            OnLine,
            OnError,
            cancellationToken).ConfigureAwait(false);

        if (result.FailedToStart)
        {
            return ErrorCatalog.CscMissing();
        }

        if (result.TimedOut)
        {
            return ErrorCatalog.CompileTimeout(Math.Max(15, timeoutSeconds));
        }

        if (result.ExitCode != 0)
        {
            logger.Write(LogLevel.Error, source, $"csc exited with code {result.ExitCode}.");
            return ErrorCatalog.CscFailed(result.ExitCode);
        }

        return null;
    }

    private static byte[] Xor(byte[] data, byte[] mask)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ mask[i % mask.Length]);
        }

        return result;
    }

    /// <summary>The stub source is C# 5 (compiled by the .NET Framework csc): no interpolated
    /// strings, null-conditional operators, expression-bodied members or nameof.</summary>
    private static string BuildStubSource(string ks, string km, string th) => $$"""
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Reflection;
        using System.Runtime.InteropServices;
        using System.Security.Cryptography;
        using System.Text;

        internal static class Q
        {
            private const string RP = "{{ResourceName}}";
            private const string KS = "{{ks}}";
            private const string KM = "{{km}}";
            private const string TH = "{{th}}";

            [DllImport("kernel32.dll")]
            private static extern bool IsDebuggerPresent();

            private static int Main(string[] a)
            {
                if (Z())
                {
                    return 0;
                }

                byte[] b = G();
                if (b == null)
                {
                    return 1;
                }

                byte[] p = D(b, K());
                Assembly m = Assembly.Load(p);
                MethodInfo e = m.EntryPoint;
                if (e == null)
                {
                    return 1;
                }

                object[] o = null;
                if (e.GetParameters().Length == 1)
                {
                    o = new object[] { a };
                }

                object r;
                try
                {
                    r = e.Invoke(null, o);
                }
                catch (TargetInvocationException x)
                {
                    if (x.InnerException != null)
                    {
                        throw x.InnerException;
                    }

                    throw;
                }

                int? v = r as int?;
                if (v.HasValue)
                {
                    return v.Value;
                }

                return Environment.ExitCode;
            }

            private static bool Z()
            {
                try
                {
                    if (Debugger.IsAttached)
                    {
                        return true;
                    }

                    if (IsDebuggerPresent())
                    {
                        return true;
                    }

                    string[] n = X(TH).Split(';');
                    for (int i = 0; i < n.Length; i++)
                    {
                        if (n[i].Length == 0)
                        {
                            continue;
                        }

                        Process[] ps = Process.GetProcessesByName(n[i]);
                        if (ps.Length > 0)
                        {
                            for (int j = 0; j < ps.Length; j++)
                            {
                                try
                                {
                                    ps[j].Dispose();
                                }
                                catch (Exception)
                                {
                                }
                            }

                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                }

                return false;
            }

            private static string X(string v)
            {
                if (v == null || v.Length == 0)
                {
                    return string.Empty;
                }

                byte[] d = Convert.FromBase64String(v);
                byte[] m = Convert.FromBase64String(KM);
                for (int i = 0; i < d.Length; i++)
                {
                    d[i] = (byte)(d[i] ^ m[i % m.Length]);
                }

                return Encoding.UTF8.GetString(d);
            }

            private static byte[] K()
            {
                byte[] s = Convert.FromBase64String(KS);
                byte[] m = Convert.FromBase64String(KM);
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = (byte)(s[i] ^ m[i % m.Length]);
                }

                return s;
            }

            private static byte[] G()
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(RP))
                {
                    if (s == null)
                    {
                        return null;
                    }

                    using (MemoryStream o = new MemoryStream())
                    {
                        byte[] b = new byte[81920];
                        int r;
                        while ((r = s.Read(b, 0, b.Length)) > 0)
                        {
                            o.Write(b, 0, r);
                        }

                        return o.ToArray();
                    }
                }
            }

            private static byte[] D(byte[] d, byte[] k)
            {
                byte[] iv = new byte[16];
                Buffer.BlockCopy(d, 0, iv, 0, 16);
                using (Aes a = Aes.Create())
                {
                    a.KeySize = 256;
                    a.Mode = CipherMode.CBC;
                    a.Padding = PaddingMode.PKCS7;
                    a.Key = k;
                    a.IV = iv;
                    using (ICryptoTransform t = a.CreateDecryptor())
                    {
                        return t.TransformFinalBlock(d, 16, d.Length - 16);
                    }
                }
            }
        }
        """;
}
