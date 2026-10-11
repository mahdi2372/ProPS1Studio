using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Pe;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class PeFileTests
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

    private string WriteTemp(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"props1pe-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Builds a minimal but structurally valid PE image with optional CLR directory and manifest text.</summary>
    private static byte[] BuildPe(bool pe32Plus, ushort machine, uint clrRva, string? manifestText)
    {
        var data = new byte[768];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(0x80u).CopyTo(data, 0x3C);

        var pe = 0x80;
        BitConverter.GetBytes(0x00004550u).CopyTo(data, pe); // PE\0\0
        BitConverter.GetBytes(machine).CopyTo(data, pe + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(data, pe + 6); // sections
        BitConverter.GetBytes((ushort)0xF0).CopyTo(data, pe + 20); // size of optional header

        var opt = pe + 24;
        BitConverter.GetBytes(pe32Plus ? (ushort)0x20B : (ushort)0x10B).CopyTo(data, opt);

        var dataDirectoryOffset = pe32Plus ? 112 : 96;
        var clr = opt + dataDirectoryOffset + (14 * 8);
        BitConverter.GetBytes(clrRva).CopyTo(data, clr);
        BitConverter.GetBytes(0x48u).CopyTo(data, clr + 4);

        if (manifestText is not null)
        {
            Encoding.ASCII.GetBytes(manifestText).CopyTo(data, 400);
        }

        return data;
    }

    [Test]
    public void Read_MissingFile_ReturnsInvalid()
    {
        var info = PeFile.Read(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.exe"));
        Assert.That(info.IsValid, Is.False);
        Assert.That(info.Error, Does.Contain("not found"));
    }

    [Test]
    public void Read_TooSmallFile_ReturnsInvalid()
    {
        var info = PeFile.Read(WriteTemp(new byte[32]));
        Assert.That(info.IsValid, Is.False);
        Assert.That(info.Error, Does.Contain("too small"));
    }

    [Test]
    public void Read_NoMzHeader_ReturnsInvalid()
    {
        var bytes = new byte[128];
        new Random(1).NextBytes(bytes);
        bytes[0] = (byte)'X';
        var info = PeFile.Read(WriteTemp(bytes));
        Assert.That(info.IsValid, Is.False);
        Assert.That(info.Error, Does.Contain("MZ"));
    }

    [Test]
    public void Read_Pe32PlusWithClr_ReportsDotNetX64()
    {
        var info = PeFile.Read(WriteTemp(BuildPe(pe32Plus: true, (ushort)PeMachine.Amd64, clrRva: 0x2000, manifestText: null)));

        Assert.That(info.IsValid, Is.True, info.Error);
        Assert.That(info.Machine, Is.EqualTo(PeMachine.Amd64));
        Assert.That(info.IsPe32Plus, Is.True);
        Assert.That(info.IsDotNet, Is.True);
        Assert.That(info.ClrRva, Is.EqualTo(0x2000u));
        Assert.That(info.MachineDisplayName, Is.EqualTo("x64 (64-bit)"));
        Assert.That(info.FileSize, Is.EqualTo(768));
    }

    [Test]
    public void Read_NativeX86_ReportsNotDotNet()
    {
        var info = PeFile.Read(WriteTemp(BuildPe(pe32Plus: false, (ushort)PeMachine.I386, clrRva: 0, manifestText: null)));

        Assert.That(info.IsValid, Is.True, info.Error);
        Assert.That(info.Machine, Is.EqualTo(PeMachine.I386));
        Assert.That(info.IsPe32Plus, Is.False);
        Assert.That(info.IsDotNet, Is.False);
    }

    [Test]
    public void Read_UnknownMachine_ReadsButReportsUnknown()
    {
        var info = PeFile.Read(WriteTemp(BuildPe(pe32Plus: true, machine: 0x1234, clrRva: 0, manifestText: null)));
        Assert.That(info.IsValid, Is.True, info.Error);
        Assert.That(info.Machine, Is.EqualTo(PeMachine.Unknown));
        Assert.That(info.MachineDisplayName, Is.EqualTo("Unknown"));
    }

    [Test]
    public void DetectLevel_AsciiRequireAdministratorManifest()
    {
        const string manifest = """
            <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
            """;
        var level = ManifestVerifier.DetectLevel(WriteTemp(BuildPe(true, (ushort)PeMachine.Amd64, 0, manifest)));
        Assert.That(level, Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
    }

    [Test]
    public void DetectLevel_AsciiAsInvokerManifest()
    {
        const string manifest = """<requestedExecutionLevel level="asInvoker" uiAccess="false"/>""";
        var level = ManifestVerifier.DetectLevel(WriteTemp(BuildPe(true, (ushort)PeMachine.Amd64, 0, manifest)));
        Assert.That(level, Is.EqualTo(RequestedExecutionLevel.AsInvoker));
    }

    [Test]
    public void DetectLevel_NoManifest_ReturnsNone()
    {
        var level = ManifestVerifier.DetectLevel(WriteTemp(BuildPe(true, (ushort)PeMachine.Amd64, 0, manifestText: null)));
        Assert.That(level, Is.EqualTo(RequestedExecutionLevel.None));
    }

    [Test]
    public void DetectLevel_Utf16Manifest_IsDetected()
    {
        var manifest = Encoding.Unicode.GetBytes(
            "<requestedExecutionLevel level=\"requireAdministrator\" uiAccess=\"false\"/>");
        var data = BuildPe(true, (ushort)PeMachine.Amd64, 0, manifestText: null);
        manifest.CopyTo(data, 400);
        var level = ManifestVerifier.DetectLevel(WriteTemp(data));
        Assert.That(level, Is.EqualTo(RequestedExecutionLevel.RequireAdministrator));
    }

    [Test]
    public void RequestsAdministrator_TrueOnlyForRequireAdministrator()
    {
        var admin = WriteTemp(BuildPe(true, (ushort)PeMachine.Amd64, 0,
            """<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>"""));
        var asInvoker = WriteTemp(BuildPe(true, (ushort)PeMachine.Amd64, 0,
            """<requestedExecutionLevel level="asInvoker" uiAccess="false"/>"""));

        Assert.That(ManifestVerifier.RequestsAdministrator(admin), Is.True);
        Assert.That(ManifestVerifier.RequestsAdministrator(asInvoker), Is.False);
    }
}
