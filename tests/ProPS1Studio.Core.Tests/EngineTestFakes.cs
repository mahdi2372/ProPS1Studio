using System.Text;
using System.Text.RegularExpressions;
using ProPS1Studio.Core.Processes;

namespace ProPS1Studio.Core.Tests;

/// <summary>Deterministic IProcessRunner for engine unit tests: records calls and lets each
/// test decide the result, including fabricating a minimal PE as "compiler output".</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = new();
    public List<string> ScriptTexts { get; } = new();

    public Func<ProcessSpec, ProcessResult> Handler { get; set; } =
        _ => new ProcessResult { ExitCode = 0, Duration = TimeSpan.Zero };

    public Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromException<ProcessResult>(new OperationCanceledException(cancellationToken));
        }

        Calls.Add(spec);
        var scriptPath = GetScriptPath(spec);
        if (scriptPath is not null && File.Exists(scriptPath))
        {
            ScriptTexts.Add(File.ReadAllText(scriptPath));
        }

        return Task.FromResult(Handler(spec));
    }

    private static string? GetScriptPath(ProcessSpec spec)
    {
        var args = spec.Arguments.ToList();
        var idx = args.IndexOf("-File");
        return idx >= 0 && idx + 1 < args.Count ? args[idx + 1] : null;
    }

    /// <summary>Handler that fabricates the output file a ps2exe wrapper script would produce.</summary>
    public FakeProcessRunner ProducesPeFromScript(bool withClr = false, string level = "asInvoker")
    {
        Handler = spec =>
        {
            var match = Regex.Match(ScriptTexts.Last(), @"outputFile\s*=\s*'([^']*)'");
            if (match.Success)
            {
                File.WriteAllBytes(match.Groups[1].Value, TestPe.Build(withClr, level));
            }

            return new ProcessResult { ExitCode = 0, Duration = TimeSpan.Zero };
        };
        return this;
    }

    /// <summary>Handler that fabricates the csc compile output at the path passed via /out:.</summary>
    public FakeProcessRunner ProducesPeFromCsc(bool withClr = true, string level = "asInvoker")
    {
        Handler = spec =>
        {
            foreach (var arg in spec.Arguments)
            {
                if (arg.StartsWith("/out:", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(arg["/out:".Length..], TestPe.Build(withClr, level));
                    break;
                }
            }

            return new ProcessResult { ExitCode = 0, Duration = TimeSpan.Zero };
        };
        return this;
    }
}

/// <summary>A minimal but structurally valid PE32 (x86) with an embedded asInvoker manifest,
/// enough for PeFile.Read and ManifestVerifier.DetectLevel to succeed in unit tests.</summary>
internal static class TestPe
{
    public const string AsInvokerManifest =
        "<assembly manifestVersion=\"1.0\"><trustInfo><security><requestedPrivileges>" +
        "<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"/>" +
        "</requestedPrivileges></security></trustInfo></assembly>";

    public static byte[] Build(bool withClr, string level = "asInvoker")
    {
        const int peOffset = 0x80;
        const int optionalHeaderSize = 0xE0;
        // PE32 optional header: data directories start at offset 96; COM descriptor = index 14.
        const int clrDirOffset = peOffset + 24 + 96 + (14 * 8);
        const int totalSize = clrDirOffset + 8 + 0x100;

        var bytes = new byte[totalSize];

        bytes[0] = 0x4D; // 'MZ'
        bytes[1] = 0x5A;
        BitConverter.GetBytes((uint)peOffset).CopyTo(bytes, 0x3C); // e_lfanew

        bytes[peOffset] = 0x50; // 'PE\0\0'
        bytes[peOffset + 1] = 0x45;
        bytes[peOffset + 3] = 0x00;
        BitConverter.GetBytes((ushort)0x014C).CopyTo(bytes, peOffset + 4); // machine: I386
        BitConverter.GetBytes((ushort)optionalHeaderSize).CopyTo(bytes, peOffset + 20);
        BitConverter.GetBytes((ushort)0x010B).CopyTo(bytes, peOffset + 24); // PE32 magic

        if (withClr)
        {
            BitConverter.GetBytes((uint)0x2000).CopyTo(bytes, clrDirOffset); // CLR RVA
            BitConverter.GetBytes((uint)0x100).CopyTo(bytes, clrDirOffset + 4); // CLR size
        }

        var manifestText = AsInvokerManifest.Replace("level=\"asInvoker\"", $"level=\"{level}\"");
        var manifest = Encoding.UTF8.GetBytes(manifestText);
        Buffer.BlockCopy(manifest, 0, bytes, totalSize - manifest.Length - 1, manifest.Length);
        return bytes;
    }
}
