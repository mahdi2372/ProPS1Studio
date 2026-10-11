using System.Text;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Errors;

namespace ProPS1Studio.Core.BatToExe;

/// <summary>
/// Validates that a selected file is a readable text batch script (.bat/.cmd) before conversion.
/// Treats script content as untrusted input; never executes it.
/// </summary>
public static class BatValidator
{
    /// <summary>Hard cap for the batch file itself (bundled extra files have their own cap).</summary>
    public const long MaxInputBytes = 10L * 1024 * 1024;

    private const int ReadCapBytes = 4 * 1024 * 1024;
    private static readonly string[] BatchExtensions = { ".bat", ".cmd" };

    public static ScriptValidationResult Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Invalid(new AppError(
                ErrorCode.InputNotFound,
                "No batch file was selected.",
                "Browse to a .bat or .cmd file, or drag one onto the window."));
        }

        if (!File.Exists(path))
        {
            return Invalid(ErrorCatalog.InputNotFound(path));
        }

        var extension = Path.GetExtension(path);
        if (!BatchExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Invalid(new AppError(
                ErrorCode.InputNotScript,
                $"The selected file is not a batch script (.bat or .cmd): {path}",
                "Select a file with the .bat or .cmd extension."));
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex)
        {
            return Invalid(ErrorCatalog.InputUnreadable(path, ex.Message));
        }

        if (info.Length == 0)
        {
            return Invalid(new AppError(
                ErrorCode.InputEmpty,
                $"The selected batch file is empty (0 bytes): {path}",
                "Add batch commands to the file, or pick a different file."));
        }

        if (info.Length > MaxInputBytes)
        {
            return Invalid(ErrorCatalog.InputTooLarge(path, info.Length, MaxInputBytes));
        }

        byte[] bytes;
        try
        {
            var maxRead = (int)Math.Min(info.Length, ReadCapBytes);
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
            return Invalid(ErrorCatalog.InputUnreadable(path, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Invalid(ErrorCatalog.InputUnreadable(path, ex.Message));
        }

        var encoding = ScriptValidator.DetectEncoding(bytes, out var bomLength);
        if (encoding == ScriptEncodingKind.BinarySuspect)
        {
            return Invalid(BinaryError(path));
        }

        var text = Decode(bytes, encoding, bomLength);
        if (text is null || text.Contains('\0'))
        {
            return Invalid(BinaryError(path));
        }

        var warnings = new List<string>();
        switch (encoding)
        {
            case ScriptEncodingKind.Utf16LeBom:
            case ScriptEncodingKind.Utf16BeBom:
                warnings.Add("The batch file uses UTF-16 encoding. cmd.exe may misread non-ASCII characters; saving it as ANSI or UTF-8 is more reliable.");
                break;
            case ScriptEncodingKind.Utf8NoBom:
                warnings.Add("The batch file is UTF-8 without a BOM. cmd.exe may misread non-ASCII characters; saving it with a UTF-8 BOM or as ANSI is more reliable.");
                break;
        }

        return new ScriptValidationResult
        {
            IsValid = true,
            Encoding = encoding,
            LengthBytes = info.Length,
            LineCount = CountLines(text),
            Warnings = warnings,
        };
    }

    private static ScriptValidationResult Invalid(AppError error) => new()
    {
        IsValid = false,
        Error = error,
    };

    private static AppError BinaryError(string path) => new(
        ErrorCode.InputBinary,
        $"The selected file looks like a binary file, not a batch script: {path}",
        "Select a plain-text .bat or .cmd file. If the script is valid but misdetected, re-save it as ANSI or UTF-8.");

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
