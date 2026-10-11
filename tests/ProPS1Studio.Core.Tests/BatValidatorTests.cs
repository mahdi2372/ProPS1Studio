using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;
using ProPS1Studio.Core.Errors;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class BatValidatorTests
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
        var path = Path.Combine(Path.GetTempPath(), $"props1bat-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Test]
    public void Validate_EmptyPath_IsRejected()
    {
        var result = BatValidator.Validate(string.Empty);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public void Validate_MissingFile_IsRejected()
    {
        var result = BatValidator.Validate(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.bat"));
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotFound));
    }

    [Test]
    public void Validate_NonBatchExtension_IsRejected()
    {
        var path = CreateTempFile(".txt", "@echo off"u8.ToArray());
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputNotScript));
    }

    [Test]
    public void Validate_EmptyFile_IsRejected()
    {
        var path = CreateTempFile(".bat", Array.Empty<byte>());
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputEmpty));
    }

    [Test]
    public void Validate_TooLargeFile_IsRejected()
    {
        var oversized = new byte[BatValidator.MaxInputBytes + 1];
        var path = CreateTempFile(".bat", oversized);
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputTooLarge));
    }

    [Test]
    public void Validate_BinaryFile_IsRejected()
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 7 == 0 ? 0 : i);
        }

        var path = CreateTempFile(".bat", bytes);
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InputBinary));
    }

    [TestCase(".bat")]
    [TestCase(".cmd")]
    [TestCase(".BAT")]
    [TestCase(".CMD")]
    public void Validate_BatchExtensions_AreAcceptedCaseInsensitively(string extension)
    {
        var path = CreateTempFile(extension, "@echo off\r\necho hi\r\n"u8.ToArray());
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.True, result.Error?.ToString());
        Assert.That(result.Error, Is.Null);
        Assert.That(result.LengthBytes, Is.EqualTo(new FileInfo(path).Length));
        Assert.That(result.LineCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Validate_Utf16WithBom_WarnsAboutCmdEncoding()
    {
        var text = "@echo off\r\necho caf\u00e9\r\n";
        var content = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var path = CreateTempFile(".bat", content);

        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.True, result.Error?.ToString());
        Assert.That(result.Warnings.Any(w => w.Contains("UTF-16")), Is.True);
    }

    [Test]
    public void Validate_Utf8WithoutBom_WarnsAboutCmdEncoding()
    {
        var path = CreateTempFile(".bat", Encoding.UTF8.GetBytes("@echo off\r\necho caf\u00e9\r\n"));
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.True, result.Error?.ToString());
        Assert.That(result.Warnings.Any(w => w.Contains("UTF-8 without a BOM")), Is.True);
    }

    [Test]
    public void Validate_AsciiFile_HasNoEncodingWarning()
    {
        var path = CreateTempFile(".bat", "@echo off\r\necho hi\r\n"u8.ToArray());
        var result = BatValidator.Validate(path);
        Assert.That(result.IsValid, Is.True, result.Error?.ToString());
        Assert.That(result.Warnings, Is.Empty);
    }
}
