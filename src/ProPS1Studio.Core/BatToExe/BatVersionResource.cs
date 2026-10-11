using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ProPS1Studio.Core.BatToExe;

/// <summary>
/// Builds a Win32 VERSION (RT_VERSION) resource blob and applies it to a PE file.
/// Layout verified against .NET Framework csc.exe's own output: root header + 4-byte alignment,
/// VS_FIXEDFILEINFO at offset 40, string entries with wValueLength in WCHARs including the
/// terminating null, applied as name=1 / language 0x0000 (neutral) so it replaces the
/// compiler's default resource instead of adding a second one.
/// </summary>
public static class BatVersionResource
{
    private const uint FixedInfoSignature = 0xFEEF04BD;
    private const ushort RtVersion = 16;
    private const ushort VersionResourceId = 1;
    private const ushort LanguageNeutral = 0x0000;

    public static byte[] Build(BatVersionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var (major, minor, build, revision) = ParseVersion(info.FileVersion);
        var fileVersion = info.FileVersion ?? "1.0.0.0";

        var root = new VersionNode("VS_VERSION_INFO", BuildFixedInfo(major, minor, build, revision), 52, 0);

        var stringTable = new VersionNode("040904B0", null, 0, 1);
        AddString(stringTable, "FileDescription", info.Title);
        AddString(stringTable, "ProductName", info.Product);
        if (!string.IsNullOrWhiteSpace(info.Company))
        {
            AddString(stringTable, "CompanyName", info.Company);
        }

        if (!string.IsNullOrWhiteSpace(info.Copyright))
        {
            AddString(stringTable, "LegalCopyright", info.Copyright);
        }

        AddString(stringTable, "FileVersion", fileVersion);
        AddString(stringTable, "OriginalFilename", info.OriginalFilename);

        var stringFileInfo = new VersionNode("StringFileInfo", null, 0, 1);
        stringFileInfo.Children.Add(stringTable);

        var translation = new VersionNode("Translation", new byte[] { 0x09, 0x04, 0xB0, 0x04 }, 4, 0);
        var varFileInfo = new VersionNode("VarFileInfo", null, 0, 1);
        varFileInfo.Children.Add(translation);

        root.Children.Add(stringFileInfo);
        root.Children.Add(varFileInfo);

        using var stream = new MemoryStream();
        WriteNode(stream, root);
        var blob = stream.ToArray();
        if (blob.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException($"The version resource is too large ({blob.Length} bytes).");
        }

        return blob;
    }

    public static (int Major, int Minor, int Build, int Revision) ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return (1, 0, 0, 0);
        }

        var parts = version.Split('.');
        if (parts.Length > 4)
        {
            throw new ArgumentException($"Version '{version}' has more than four parts.", nameof(version));
        }

        var values = new[] { 0, 0, 0, 0 };
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value))
            {
                throw new ArgumentException($"Version '{version}' is not numeric at part {i + 1}.", nameof(version));
            }

            values[i] = Math.Clamp(value, 0, 65535);
        }

        if (values[0] == 0 && values[1] == 0 && values[2] == 0 && values[3] == 0)
        {
            values[0] = 1;
        }

        return (values[0], values[1], values[2], values[3]);
    }

    public static void Apply(string path, byte[] blob)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length == 0)
        {
            throw new ArgumentException("The version resource blob is empty.", nameof(blob));
        }

        var update = BeginUpdateResourceW(path, bDeleteExistingResources: false);
        if (update == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open '{path}' to write version information.");
        }

        var type = (IntPtr)RtVersion;
        var name = (IntPtr)VersionResourceId;
        if (!UpdateResourceW(update, type, name, LanguageNeutral, blob, (uint)blob.Length))
        {
            var error = Marshal.GetLastWin32Error();
            EndUpdateResourceW(update, fDiscard: true);
            throw new Win32Exception(error, $"Cannot write version information into '{path}'.");
        }

        if (!EndUpdateResourceW(update, fDiscard: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot finalize version information in '{path}'.");
        }
    }

    private static void AddString(VersionNode parent, string key, string value)
    {
        var text = value ?? string.Empty;
        parent.Children.Add(new VersionNode(
            key,
            Encoding.Unicode.GetBytes(text + "\0"),
            checked((ushort)(text.Length + 1)),
            1));
    }

    private static byte[] BuildFixedInfo(int major, int minor, int build, int revision)
    {
        var buffer = new byte[52];
        using var stream = new MemoryStream(buffer);
        using var writer = new BinaryWriter(stream);
        writer.Write(FixedInfoSignature);
        writer.Write(0x00010000u);
        writer.Write((uint)((major << 16) | minor));
        writer.Write((uint)((build << 16) | revision));
        writer.Write((uint)((major << 16) | minor));
        writer.Write((uint)((build << 16) | revision));
        writer.Write(0x0000003Fu);
        writer.Write(0u);
        writer.Write(0x00040004u);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        return buffer;
    }

    private static void WriteNode(Stream stream, VersionNode node)
    {
        Align4(stream);
        var start = stream.Position;
        WriteUInt16(stream, 0);
        WriteUInt16(stream, node.ValueLength);
        WriteUInt16(stream, node.Type);
        WriteUtf16Z(stream, node.Key);
        Align4(stream);
        if (node.Value is { Length: > 0 })
        {
            stream.Write(node.Value, 0, node.Value.Length);
            Align4(stream);
        }

        foreach (var child in node.Children)
        {
            WriteNode(stream, child);
        }

        Align4(stream);
        var end = stream.Position;
        var length = end - start;
        if (length > ushort.MaxValue)
        {
            throw new InvalidOperationException($"A version resource node exceeded {ushort.MaxValue} bytes.");
        }

        stream.Position = start;
        WriteUInt16(stream, (ushort)length);
        stream.Position = end;
    }

    private static void Align4(Stream stream)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private static void WriteUtf16Z(Stream stream, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        stream.Write(bytes, 0, bytes.Length);
    }

    private sealed class VersionNode
    {
        public VersionNode(string key, byte[]? value, ushort valueLength, ushort type)
        {
            Key = key;
            Value = value;
            ValueLength = valueLength;
            Type = type;
        }

        public string Key { get; }
        public byte[]? Value { get; }
        public ushort ValueLength { get; }
        public ushort Type { get; }
        public List<VersionNode> Children { get; } = new();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr BeginUpdateResourceW(string fileName, [MarshalAs(UnmanagedType.Bool)] bool bDeleteExistingResources);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(
        IntPtr hUpdate,
        IntPtr lpType,
        IntPtr lpName,
        ushort wLanguage,
        byte[] lpData,
        uint cb);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResourceW(IntPtr hUpdate, [MarshalAs(UnmanagedType.Bool)] bool fDiscard);
}
