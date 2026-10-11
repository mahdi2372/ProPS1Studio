using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Errors;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class ScriptValidatorTests
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

    private string CreateTempFile(string extension, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Test]
    public void Validate_EmptyPath_IsRejected()
    {
        var result = ScriptValidator.Validate(string.Empty);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public void Validate_MissingFile_IsRejected()
    {
        var result = ScriptValidator.Validate(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.ps1"));
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public void Validate_NonPs1Extension_IsRejected()
    {
        var path = CreateTempFile(".txt", "Write-Host 1"u8.ToArray());
        var result = ScriptValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotScript));
    }

    [Test]
    public void Validate_EmptyFile_IsRejected()
    {
        var path = CreateTempFile(".ps1", Array.Empty<byte>());
        var result = ScriptValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputEmpty));
    }

    [Test]
    public void Validate_BinaryFile_IsRejected()
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 7 == 0 ? 0 : i);
        }

        var path = CreateTempFile(".ps1", bytes);
        var result = ScriptValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputBinary));
    }

    [Test]
    public void Validate_PlainAsciiScript_IsValidWithNoWarnings()
    {
        var path = CreateTempFile(".ps1", Encoding.ASCII.GetBytes("Write-Host 'hello'\nGet-Date\n"));
        var result = ScriptValidator.Validate(path);

        Assert.That(result.IsValid, Is.True, result.Error?.Message);
        Assert.That(result.Encoding, Is.EqualTo(ScriptEncodingKind.AsciiCompatible));
        Assert.That(result.LineCount, Is.EqualTo(2));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Validate_Utf8BomScript_IsValidAndRecognized()
    {
        var content = Encoding.UTF8.GetPreamble().Concat("Write-Host 'héllo'"u8.ToArray()).ToArray();
        var path = CreateTempFile(".ps1", content);
        var result = ScriptValidator.Validate(path);

        Assert.That(result.IsValid, Is.True, result.Error?.Message);
        Assert.That(result.Encoding, Is.EqualTo(ScriptEncodingKind.Utf8Bom));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Validate_Utf8NoBom_ProducesWarning()
    {
        var path = CreateTempFile(".ps1", "Write-Host 'héllo'"u8.ToArray());
        var result = ScriptValidator.Validate(path);

        Assert.That(result.IsValid, Is.True, result.Error?.Message);
        Assert.That(result.Encoding, Is.EqualTo(ScriptEncodingKind.Utf8NoBom));
        Assert.That(result.Warnings, Has.Some.Contains("UTF-8"));
    }

    [Test]
    public void Validate_Utf16LeBom_ProducesWarning()
    {
        var path = CreateTempFile(".ps1", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Write-Host 1")).ToArray());
        var result = ScriptValidator.Validate(path);

        Assert.That(result.IsValid, Is.True, result.Error?.Message);
        Assert.That(result.Encoding, Is.EqualTo(ScriptEncodingKind.Utf16LeBom));
        Assert.That(result.Warnings, Has.Some.Contains("UTF-16"));
    }

    [TestCase(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, ScriptEncodingKind.AsciiCompatible)]
    [TestCase(new byte[] { 0xEF, 0xBB, 0xBF, 0x57 }, ScriptEncodingKind.Utf8Bom)]
    [TestCase(new byte[] { 0xFF, 0xFE, 0x57, 0x00 }, ScriptEncodingKind.Utf16LeBom)]
    [TestCase(new byte[] { 0xFE, 0xFF, 0x00, 0x57 }, ScriptEncodingKind.Utf16BeBom)]
    public void DetectEncoding_RecognizesSignatures(byte[] bytes, ScriptEncodingKind expected)
    {
        var kind = ScriptValidator.DetectEncoding(bytes, out var bomLength);
        Assert.That(kind, Is.EqualTo(expected));
        if (expected is ScriptEncodingKind.Utf8Bom or ScriptEncodingKind.Utf16LeBom or ScriptEncodingKind.Utf16BeBom)
        {
            Assert.That(bomLength, Is.GreaterThan(0));
        }
        else
        {
            Assert.That(bomLength, Is.Zero);
        }
    }

    [Test]
    public void IsValidUtf8_AcceptsValidAndRejectsInvalid()
    {
        Assert.That(ScriptValidator.IsValidUtf8("héllo wörld"u8), Is.True);
        Assert.That(ScriptValidator.IsValidUtf8(new byte[] { 0x48, 0x69, 0xFF, 0xFE, 0x21 }), Is.False);
    }
}
