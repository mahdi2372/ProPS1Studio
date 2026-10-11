using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Conversion;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class ScriptEncryptorTests
{
    private const string Marker = "PROPS1-SECRET-SOURCE-MARKER-7f3a9c";

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

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

    [Test]
    public void BuildLoader_ContainsNoPlaintextOfTheSource()
    {
        var source = Utf8($"Write-Host '{Marker}'\n$password = '{Marker}'\n");

        var loader = ScriptEncryptor.BuildLoader(source);

        Assert.That(loader, Does.Not.Contain(Marker), "raw plaintext must not survive");
        Assert.That(loader, Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Marker))),
            "plaintext must not survive as plain base64 either");
        Assert.That(BytesContain(Encoding.UTF8.GetBytes(loader), Encoding.UTF8.GetBytes(Marker)), Is.False);
        Assert.That(BytesContain(Encoding.Unicode.GetBytes(loader), Encoding.Unicode.GetBytes(Marker)), Is.False);
    }

    [Test]
    public void BuildLoader_RoundtripIsLossless_ForUtf8BomUtf16BomAndAnsiBytes()
    {
        var utf16 = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("utf16 content 中文 é\n")).ToArray();
        var samples = new[]
        {
            Utf8("plain ascii script\nGet-Date\n"),
            new UTF8Encoding(true).GetBytes("# é 中 utf8 bom\nWrite-Host ok\n"),
            utf16,
            // invalid as strict UTF-8: forces the loader's ANSI fallback path
            new byte[] { 0x57, 0x72, 0x69, 0x74, 0x65, 0x20, 0xE9, 0xFC, 0x0A },
        };

        foreach (var sample in samples)
        {
            var loader = ScriptEncryptor.BuildLoader(sample);
            Assert.That(ScriptEncryptor.DecryptFromLoader(loader), Is.EqualTo(sample));
        }
    }

    [Test]
    public void BuildLoader_PreservesRequiresDirectivesAtTheTop()
    {
        var source = Utf8("#Requires -Version 5.1\n#requires -RunAsAdministrator\nWrite-Host hi\n");

        var loader = ScriptEncryptor.BuildLoader(source);

        Assert.That(loader, Does.Contain("#Requires -Version 5.1"));
        Assert.That(loader, Does.Contain("#requires -RunAsAdministrator"));
        Assert.That(
            loader.IndexOf("#Requires -Version 5.1", StringComparison.Ordinal),
            Is.LessThan(loader.IndexOf("$____p", StringComparison.Ordinal)),
            "#requires must precede any executable statement");
    }

    [Test]
    public void BuildLoader_IsAsciiSafe()
    {
        var loader = ScriptEncryptor.BuildLoader(Utf8("Write-Host '中文 é'\n"));

        Assert.That(loader.All(c => c <= 0x7F), Is.True,
            "the loader must be pure ASCII so file encoding cannot corrupt it");
    }

    [Test]
    public void BuildLoader_UsesFreshRandomKeyPerBuild()
    {
        var source = Utf8("Write-Host same\n");
        var first = ScriptEncryptor.BuildLoader(source);
        var second = ScriptEncryptor.BuildLoader(source);

        Assert.That(first, Is.Not.EqualTo(second), "each build must get a fresh key/IV");
        Assert.That(ScriptEncryptor.DecryptFromLoader(first), Is.EqualTo(source));
        Assert.That(ScriptEncryptor.DecryptFromLoader(second), Is.EqualTo(source));
    }

    [Test]
    public void BuildLoader_QuotesAndSpecialCharactersSurviveUnescaped()
    {
        var source = Utf8("$a = 'it''s \"quoted\"'\n& cmd /c \"x | y && z %PATH%\"\n");

        var loader = ScriptEncryptor.BuildLoader(source);

        Assert.That(loader, Does.Not.Contain("quoted"), "payload must be ciphertext, not inline text");
        Assert.That(ScriptEncryptor.DecryptFromLoader(loader), Is.EqualTo(source));
    }

    [Test]
    public void BuildLoader_ExecutesThroughScriptblockAndForwardsArgs()
    {
        var loader = ScriptEncryptor.BuildLoader(Utf8("Write-Output 'x'\n"));

        Assert.That(loader, Does.Contain("[scriptblock]::Create("));
        Assert.That(loader, Does.Contain("& $____s @args"));
        Assert.That(loader, Does.Contain("[Convert]::FromBase64String("));
    }

    [Test]
    public void BuildLoader_EmptySource_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScriptEncryptor.BuildLoader(Array.Empty<byte>()));
    }

    [Test]
    public void ExtractRequiresLines_IgnoresMentionsInsideComments()
    {
        var source = Utf8("# see the #requires docs\nWrite-Host hi\n");

        Assert.That(ScriptEncryptor.ExtractRequiresLines(source), Is.Empty);
    }
}
