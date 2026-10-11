using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class BatLauncherSourceTests
{
    private static BatLauncherOptions MakeOptions(
        BatPayload? payload = null,
        string passphrase = "",
        IReadOnlyList<BatEmbeddedFile>? extraFiles = null,
        bool noConsole = false,
        bool captureLog = false,
        bool keepOpen = false,
        bool forwardArgs = true,
        bool runFromTemp = false,
        bool keepTemp = false,
        string? consoleTitle = null,
        string? consoleColor = null)
        => new(
            ScriptFileName: "run.bat",
            Payload: payload ?? new BatPayload(BatPayloadCodec.ModeGzip, "QUJD"),
            Passphrase: passphrase,
            ExtraFiles: extraFiles ?? Array.Empty<BatEmbeddedFile>(),
            NoConsole: noConsole,
            CaptureOutputLog: captureLog,
            KeepConsoleOpen: keepOpen,
            ForwardArguments: forwardArgs,
            RunFromTempDirectory: runFromTemp,
            KeepTempFile: keepTemp,
            ConsoleTitle: consoleTitle,
            ConsoleColorHex: consoleColor);

    /// <summary>Removes quotes, whitespace, and '+' so chunked literals can be compared as one string.</summary>
    private static string Strip(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch != '"' && ch != '+' && !char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    [Test]
    public void Build_ProducesNamespacedLauncherWithSalt()
    {
        var source = BatLauncherSource.Build(MakeOptions());
        Assert.That(source, Does.Contain("namespace ProPS1Studio.BatExe"));
        Assert.That(source, Does.Contain("ProPS1.BatToExe.v1"));
        Assert.That(source, Does.Contain("private const string ScriptFileName"));
        // The script file name is XOR-maskified; no plaintext copy may survive.
        Assert.That(source, Does.Not.Contain("run.bat"));
        Assert.That(source, Does.Contain("U(ScriptFileName)"));
    }

    [Test]
    public void Build_EmbedsFullPayload_AsContiguousBase64()
    {
        var payload = BatPayloadCodec.Encode(Encoding.UTF8.GetBytes("@echo off\r\necho hi\r\n"),
            compress: true, passphrase: null, encrypt: false);
        var source = BatLauncherSource.Build(MakeOptions(payload: payload));
        Assert.That(Strip(source), Does.Contain(Strip(payload.Data)));
    }

    [Test]
    public void Build_GeneratedSource_CanDecryptAesPayload()
    {
        var payload = BatPayloadCodec.Encode(Encoding.UTF8.GetBytes("@echo off\r\necho hi\r\n"),
            compress: true, passphrase: null, encrypt: true);
        var source = BatLauncherSource.Build(MakeOptions(payload: payload));

        Assert.That(payload.Mode, Is.EqualTo(BatPayloadCodec.ModeGzipAes));
        Assert.That(source, Does.Contain("private const string PayloadMode"));
        // The mode string itself is obfuscated: the exact plaintext literal must be gone
        // (quotes/spaces can never occur inside a base64 constant).
        Assert.That(source, Does.Not.Contain("PayloadMode = \"gza\""));
        Assert.That(source, Does.Contain("U(PayloadMode)"));
        Assert.That(source, Does.Contain("private static byte[] AesDecrypt"));
        Assert.That(source, Does.Contain("TransformFinalBlock"));
        Assert.That(source, Does.Contain("aes.CreateDecryptor()"));
        Assert.That(source, Does.Contain("modeCore.EndsWith(\"a\", StringComparison.Ordinal)"));
    }

    [Test]
    public void Build_GeneratedSource_DecodesEveryLegacyAndAesMode()
    {
        var source = BatLauncherSource.Build(MakeOptions());
        // Mode parsing must strip the optional AES suffix first, then the XOR suffix,
        // so "gzxa" (gzip+xor+aes) and all legacy modes decode correctly.
        Assert.That(source, Does.Contain("var encrypted = modeCore.EndsWith(\"a\", StringComparison.Ordinal);"));
        Assert.That(source, Does.Contain("var obfuscated = modeCore.EndsWith(\"x\", StringComparison.Ordinal);"));
        Assert.That(source, Does.Contain("var compressed = modeCore == \"gz\";"));
    }

    [Test]
    public void Build_ChunksLongPayloadIntoLiteralsWithinLimit()
    {
        var payloadData = new string('A', BatLauncherSource.PayloadChunkSize * 2 + 500);
        var payload = new BatPayload(BatPayloadCodec.ModeRaw, payloadData);
        var source = BatLauncherSource.Build(MakeOptions(payload: payload));

        var line = source.Split('\n')
            .First(l => l.Contains("private const string PayloadData", StringComparison.Ordinal));
        var segments = line.Split('"');
        var chunks = new List<string>();
        for (var i = 1; i < segments.Length; i += 2)
        {
            chunks.Add(segments[i]);
        }

        Assert.That(chunks.Count, Is.GreaterThanOrEqualTo(2), "long payloads must be split");
        Assert.That(chunks.All(c => c.Length <= BatLauncherSource.PayloadChunkSize), Is.True);
        Assert.That(string.Concat(chunks), Is.EqualTo(payloadData));
    }

    [Test]
    public void Build_GeneratedSource_HasNoModernCSharpSyntax()
    {
        var source = BatLauncherSource.Build(MakeOptions());
        Assert.That(source, Does.Not.Contain("$\""));
        Assert.That(source, Does.Not.Contain("?."));
        Assert.That(source, Does.Not.Contain("=> "));
        Assert.That(source, Does.Not.Contain("nameof("));
    }

    [Test]
    public void Build_BooleanOptions_AreEmittedAsIntConstants()
    {
        var source = BatLauncherSource.Build(MakeOptions(
            noConsole: true, captureLog: true, keepOpen: true,
            forwardArgs: false, runFromTemp: true, keepTemp: true));

        Assert.That(source, Does.Contain("private const int OptNoConsole = 1;"));
        Assert.That(source, Does.Contain("private const int OptCaptureLog = 1;"));
        Assert.That(source, Does.Contain("private const int OptKeepOpen = 1;"));
        Assert.That(source, Does.Contain("private const int OptForwardArgs = 0;"));
        Assert.That(source, Does.Contain("private const int OptRunFromTemp = 1;"));
        Assert.That(source, Does.Contain("private const int OptKeepTemp = 1;"));
        Assert.That(source, Does.Contain("private const int OptExtraFileCount = 0;"));
    }

    [Test]
    public void Build_IncludesConsoleTitleAndColor()
    {
        var source = BatLauncherSource.Build(MakeOptions(consoleTitle: "My Title", consoleColor: "1F"));
        Assert.That(source, Does.Contain("private const string OptColor"));
        Assert.That(source, Does.Contain("OptTitleValue = U(OptTitle)"));
        Assert.That(source, Does.Contain("OptColorValue = U(OptColor)"));
        // Neither console value may survive as a readable literal.
        Assert.That(source, Does.Not.Contain("My Title"));
        Assert.That(source, Does.Not.Contain("OptColor = \"1F\""));
    }

    [Test]
    public void Build_EmbedsExtraFileTable()
    {
        var files = new[]
        {
            new BatEmbeddedFile("data.txt", BatPayloadCodec.ModeGzip, "QUJD"),
            new BatEmbeddedFile("note.md", BatPayloadCodec.ModeRaw, "BA=="),
        };
        var source = BatLauncherSource.Build(MakeOptions(extraFiles: files));

        Assert.That(source, Does.Contain("private const int OptExtraFileCount = 2;"));
        // The whole table (file names, modes, data) is XOR-maskified as one value.
        Assert.That(source, Does.Not.Contain("data.txt"));
        Assert.That(source, Does.Not.Contain("note.md"));
        Assert.That(source, Does.Contain("ExtraFileTableValue.Split('\\n')"));
        Assert.That(source, Does.Contain("U(ExtraFileTable)"));
    }

    [Test]
    public void Build_EmbedsPassphrase()
    {
        var source = BatLauncherSource.Build(MakeOptions(passphrase: "hunter2"));
        Assert.That(source, Does.Contain("private const string PassPhrase"));
        // No plaintext passphrase in the source - only its masked form plus the U() decoder.
        Assert.That(source, Does.Not.Contain("hunter2"));
        Assert.That(source, Does.Contain("U(PassPhrase)"));
    }

    [Test]
    public void Build_EmptyScriptFileName_Throws()
    {
        var options = MakeOptions() with { ScriptFileName = "  " };
        Assert.Throws<ArgumentException>(() => BatLauncherSource.Build(options));
    }

    [Test]
    public void Build_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BatLauncherSource.Build(null!));
    }

    [Test]
    public void CsEscape_EscapesQuotesBackslashesAndControls()
    {
        Assert.That(BatLauncherSource.CsEscape("a\"b\\c"), Is.EqualTo("a\\\"b\\\\c"));
        Assert.That(BatLauncherSource.CsEscape("\r\n\t"), Is.EqualTo("\\r\\n\\t"));
        Assert.That(BatLauncherSource.CsEscape("\u0001\u001f"), Is.EqualTo("\\u0001\\u001f"));
    }

    [Test]
    public void CsEscape_EscapesSurrogateHalvesIndividually()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        Assert.That(emoji.Length, Is.EqualTo(2));
        Assert.That(BatLauncherSource.CsEscape(emoji), Is.EqualTo("\\ud83d\\ude00"));
    }

    [Test]
    public void CsEscape_LeavesPlainTextAlone()
    {
        Assert.That(BatLauncherSource.CsEscape("plain text 123"), Is.EqualTo("plain text 123"));
    }
}
