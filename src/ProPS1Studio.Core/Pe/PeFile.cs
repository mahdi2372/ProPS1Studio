namespace ProPS1Studio.Core.Pe;

public enum PeMachine : ushort
{
    Unknown = 0,
    I386 = 0x014c,
    Amd64 = 0x8664,
    Arm64 = 0xAA64,
    Arm = 0x01c0,
}

/// <summary>Parsed PE header facts used for verification and compatibility checks.</summary>
public sealed record PeInfo
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public PeMachine Machine { get; init; }
    public bool IsPe32Plus { get; init; }
    public uint ClrRva { get; init; }
    public uint ClrSize { get; init; }
    public bool IsDotNet => ClrRva != 0;
    public long FileSize { get; init; }

    public string MachineDisplayName => Machine switch
    {
        PeMachine.I386 => "x86 (32-bit)",
        PeMachine.Amd64 => "x64 (64-bit)",
        PeMachine.Arm64 => "ARM64",
        PeMachine.Arm => "ARM",
        _ => "Unknown",
    };
}

public enum RequestedExecutionLevel
{
    None,
    AsInvoker,
    RequireAdministrator,
    HighestAvailable,
    Unknown,
}

/// <summary>Minimal, allocation-conscious PE reader (no external dependencies).</summary>
public static class PeFile
{
    public static PeInfo Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new PeInfo { IsValid = false, Error = "File not found." };
            }

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64)
            {
                return new PeInfo { IsValid = false, Error = "File is too small to be a PE file.", FileSize = stream.Length };
            }

            if (reader.ReadUInt16() != 0x5A4D) // 'MZ'
            {
                return new PeInfo { IsValid = false, Error = "Missing MZ header.", FileSize = stream.Length };
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadUInt32();
            if (peOffset == 0 || peOffset + 24 > stream.Length)
            {
                return new PeInfo { IsValid = false, Error = "Invalid PE header offset.", FileSize = stream.Length };
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) // 'PE\0\0'
            {
                return new PeInfo { IsValid = false, Error = "Missing PE signature.", FileSize = stream.Length };
            }

            var machineValue = reader.ReadUInt16();
            _ = reader.ReadUInt16(); // number of sections
            _ = reader.ReadUInt32(); // time date stamp
            _ = reader.ReadUInt32(); // pointer to symbol table
            _ = reader.ReadUInt32(); // number of symbols
            var sizeOfOptionalHeader = reader.ReadUInt16();
            _ = reader.ReadUInt16(); // characteristics

            stream.Position = peOffset + 24;
            var magic = reader.ReadUInt16();
            var isPe32Plus = magic == 0x20B;
            if (magic != 0x10B && magic != 0x20B)
            {
                return new PeInfo { IsValid = false, Error = $"Unknown optional header magic 0x{magic:X}.", FileSize = stream.Length };
            }

            var dataDirectoryOffset = isPe32Plus ? 112 : 96;
            var clrDirectoryPosition = peOffset + 24 + dataDirectoryOffset + (14 * 8); // COM descriptor = index 14
            uint clrRva = 0, clrSize = 0;
            if (clrDirectoryPosition + 8 <= stream.Length &&
                clrDirectoryPosition + 8 <= peOffset + 24 + sizeOfOptionalHeader)
            {
                stream.Position = clrDirectoryPosition;
                clrRva = reader.ReadUInt32();
                clrSize = reader.ReadUInt32();
            }

            return new PeInfo
            {
                IsValid = true,
                Machine = Enum.IsDefined(typeof(PeMachine), (ushort)machineValue) ? (PeMachine)machineValue : PeMachine.Unknown,
                IsPe32Plus = isPe32Plus,
                ClrRva = clrRva,
                ClrSize = clrSize,
                FileSize = stream.Length,
            };
        }
        catch (IOException ex)
        {
            return new PeInfo { IsValid = false, Error = $"IO error: {ex.Message}" };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PeInfo { IsValid = false, Error = $"Access denied: {ex.Message}" };
        }
    }
}

/// <summary>Scans an executable for an embedded Win32 application manifest and its requested execution level.</summary>
public static class ManifestVerifier
{
    private static readonly byte[] LevelAscii = "requestedExecutionLevel"u8.ToArray();
    private static readonly byte[] RequireAdminAscii = "requireAdministrator"u8.ToArray();
    private static readonly byte[] AsInvokerAscii = "asInvoker"u8.ToArray();
    private static readonly byte[] HighestAscii = "highestAvailable"u8.ToArray();

    public static RequestedExecutionLevel DetectLevel(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var level = DetectInBuffer(bytes);
            if (level != RequestedExecutionLevel.None && level != RequestedExecutionLevel.Unknown)
            {
                return level;
            }

            // Also check UTF-16LE encoded manifests.
            var utf16 = System.Text.Encoding.Unicode.GetString(bytes);
            if (utf16.Contains("requestedExecutionLevel", StringComparison.OrdinalIgnoreCase))
            {
                if (utf16.Contains("requireAdministrator", StringComparison.OrdinalIgnoreCase))
                {
                    return RequestedExecutionLevel.RequireAdministrator;
                }

                if (utf16.Contains("asInvoker", StringComparison.OrdinalIgnoreCase))
                {
                    return RequestedExecutionLevel.AsInvoker;
                }

                if (utf16.Contains("highestAvailable", StringComparison.OrdinalIgnoreCase))
                {
                    return RequestedExecutionLevel.HighestAvailable;
                }

                return RequestedExecutionLevel.Unknown;
            }

            return RequestedExecutionLevel.None;
        }
        catch
        {
            return RequestedExecutionLevel.None;
        }
    }

    public static bool RequestsAdministrator(string path) =>
        DetectLevel(path) == RequestedExecutionLevel.RequireAdministrator;

    private static RequestedExecutionLevel DetectInBuffer(byte[] bytes)
    {
        var marker = IndexOf(bytes, LevelAscii);
        if (marker < 0)
        {
            return RequestedExecutionLevel.None;
        }

        // The level attribute appears next to the marker; search a window around it.
        var windowStart = Math.Max(0, marker - 128);
        var windowEnd = Math.Min(bytes.Length, marker + 256);
        var window = new byte[windowEnd - windowStart];
        Array.Copy(bytes, windowStart, window, 0, window.Length);

        if (IndexOf(window, RequireAdminAscii) >= 0)
        {
            return RequestedExecutionLevel.RequireAdministrator;
        }

        if (IndexOf(window, AsInvokerAscii) >= 0)
        {
            return RequestedExecutionLevel.AsInvoker;
        }

        if (IndexOf(window, HighestAscii) >= 0)
        {
            return RequestedExecutionLevel.HighestAvailable;
        }

        return RequestedExecutionLevel.Unknown;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

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
                return i;
            }
        }

        return -1;
    }
}
