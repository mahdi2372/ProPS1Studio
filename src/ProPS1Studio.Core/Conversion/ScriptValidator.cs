using System.Text;
using ProPS1Studio.Core.Errors;

namespace ProPS1Studio.Core.Conversion;

public enum ScriptEncodingKind
{
    Unknown,
    Utf8Bom,
    Utf16LeBom,
    Utf16BeBom,
    Utf8NoBom,
    AsciiCompatible,
    BinarySuspect,
}

public sealed record ScriptValidationResult
{
    public bool IsValid { get; init; }
    public AppError? Error { get; init; }
    public ScriptEncodingKind Encoding { get; init; }
    public long LengthBytes { get; init; }
    public int LineCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Validates that a selected file is a readable text PowerShell script before conversion.
/// Treats script content as untrusted input; never executes it.
/// </summary>
public static class ScriptValidator
{
    private static readonly string[] ScriptExtensions = { ".ps1" };

    public static ScriptValidationResult Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ScriptValidationResult
            {
                IsValid = false,
                Error = new AppError(ErrorCode.InputNotFound, "No script file was selected.", "Browse to a .ps1 file or drag one onto the window."),
            };
        }

        if (!File.Exists(path))
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputNotFound(path) };
        }

        var extension = Path.GetExtension(path);
        if (!ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputNotScript(path) };
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex)
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputUnreadable(path, ex.Message) };
        }

        if (info.Length == 0)
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputEmpty(path) };
        }

        byte[] bytes;
        try
        {
            // Read a bounded prefix for classification, then the full text for line counts.
            var maxRead = (int)Math.Min(info.Length, 4 * 1024 * 1024); // 4 MB cap for validation reads
            bytes = new byte[maxRead];
            using var stream = File.OpenRead(path);
            var read = stream.Read(bytes, 0, maxRead);
            if (read < bytes.Length)
            {
                Array.Resize(ref bytes, read);
            }
        }
        catch (IOException ex)
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputUnreadable(path, ex.Message) };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputUnreadable(path, ex.Message) };
        }

        var encoding = DetectEncoding(bytes, out var bomLength);
        if (encoding == ScriptEncodingKind.BinarySuspect)
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputBinary(path) };
        }

        var warnings = new List<string>();
        var text = Decode(bytes, encoding, bomLength);
        if (text is null)
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputBinary(path) };
        }

        var lineCount = CountLines(text);
        switch (encoding)
        {
            case ScriptEncodingKind.Utf16LeBom:
            case ScriptEncodingKind.Utf16BeBom:
                warnings.Add("The script uses UTF-16 encoding. It will work, but UTF-8 with BOM is the most reliable encoding for PowerShell tooling.");
                break;
            case ScriptEncodingKind.Utf8NoBom:
                warnings.Add("The script is UTF-8 without a BOM. Windows PowerShell 5.1 may misread non-ASCII characters; saving with a UTF-8 BOM is recommended.");
                break;
        }

        if (text.Contains('\0'))
        {
            return new ScriptValidationResult { IsValid = false, Error = ErrorCatalog.InputBinary(path) };
        }

        return new ScriptValidationResult
        {
            IsValid = true,
            Encoding = encoding,
            LengthBytes = info.Length,
            LineCount = lineCount,
            Warnings = warnings,
        };
    }

    public static ScriptEncodingKind DetectEncoding(ReadOnlySpan<byte> bytes, out int bomLength)
    {
        bomLength = 0;
        if (bytes.Length == 0)
        {
            return ScriptEncodingKind.Unknown;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bomLength = 3;
            return ScriptEncodingKind.Utf8Bom;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            bomLength = 2;
            return ScriptEncodingKind.Utf16LeBom;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            bomLength = 2;
            return ScriptEncodingKind.Utf16BeBom;
        }

        // Heuristic: NUL bytes in the first block strongly indicate UTF-16 without BOM or binary data.
        var sample = bytes[..Math.Min(bytes.Length, 4096)];
        var nullCount = 0;
        var nonAscii = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                nullCount++;
            }

            if (b >= 0x80)
            {
                nonAscii++;
            }
        }

        if (sample.Length > 16 && nullCount > sample.Length / 8)
        {
            return ScriptEncodingKind.BinarySuspect;
        }

        if (IsValidUtf8(sample))
        {
            return nonAscii == 0 ? ScriptEncodingKind.AsciiCompatible : ScriptEncodingKind.Utf8NoBom;
        }

        // Invalid UTF-8 but no NULs: treat as legacy ANSI text (still usable, with a warning).
        return nonAscii > 0 ? ScriptEncodingKind.AsciiCompatible : ScriptEncodingKind.AsciiCompatible;
    }

    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string? Decode(byte[] bytes, ScriptEncodingKind encoding, int bomLength)
    {
        try
        {
            return encoding switch
            {
                ScriptEncodingKind.Utf16LeBom => Encoding.Unicode.GetString(bytes, bomLength, bytes.Length - bomLength),
                ScriptEncodingKind.Utf16BeBom => Encoding.BigEndianUnicode.GetString(bytes, bomLength, bytes.Length - bomLength),
                _ => Encoding.UTF8.GetString(bytes, bomLength, bytes.Length - bomLength),
            };
        }
        catch
        {
            return null;
        }
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        if (text[^1] != '\n')
        {
            count++;
        }

        return count;
    }
}
