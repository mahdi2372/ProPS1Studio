using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class BatVersionResourceTests
{
    private static BatVersionInfo MakeInfo(
        string title = "My Tool",
        string product = "My Product",
        string company = "",
        string copyright = "",
        string fileVersion = "2.3.4.5",
        string originalFilename = "tool.exe")
        => new(title, "Description here", product, company, copyright, fileVersion, originalFilename);

    [TestCase("2.3.4.5", 2, 3, 4, 5)]
    [TestCase("1.0", 1, 0, 0, 0)]
    [TestCase("7", 7, 0, 0, 0)]
    [TestCase(null, 1, 0, 0, 0)]
    [TestCase("", 1, 0, 0, 0)]
    [TestCase("0.0.0.0", 1, 0, 0, 0)]
    [TestCase("99999.1.1.1", 65535, 1, 1, 1)]
    public void ParseVersion_HandlesValidAndEdgeInput(string? version, int major, int minor, int build, int revision)
    {
        Assert.That(BatVersionResource.ParseVersion(version),
            Is.EqualTo((major, minor, build, revision)));
    }

    [TestCase("1.2.3.4.5")]
    [TestCase("a.b")]
    [TestCase("1.x")]
    public void ParseVersion_RejectsMalformedInput(string version)
    {
        Assert.Throws<ArgumentException>(() => BatVersionResource.ParseVersion(version));
    }

    [Test]
    public void Build_RootHeaderMatchesBlobLayout()
    {
        var blob = BatVersionResource.Build(MakeInfo());

        var rootLength = BitConverter.ToUInt16(blob, 0);
        Assert.That(rootLength, Is.EqualTo(blob.Length));
        Assert.That(BitConverter.ToUInt16(blob, 2), Is.EqualTo(52), "wValueLength of the root node");
        Assert.That(BitConverter.ToUInt16(blob, 4), Is.EqualTo(0), "root node is a binary (wType=0) node");
        Assert.That(blob.Length % 4, Is.EqualTo(0), "the blob is 4-byte aligned");
    }

    [Test]
    public void Build_ContainsFixedFileInfoSignatureAtOffset40()
    {
        var blob = BatVersionResource.Build(MakeInfo());

        Assert.That(BitConverter.ToUInt32(blob, 40), Is.EqualTo(0xFEEF04BDu));
        Assert.That(BitConverter.ToUInt32(blob, 44), Is.EqualTo(0x00010000u), "dwStrucVersion");

        // FileVersion 2.3.4.5 -> MS=0x00020003, LS=0x00040005.
        Assert.That(BitConverter.ToUInt32(blob, 48), Is.EqualTo(0x00020003u));
        Assert.That(BitConverter.ToUInt32(blob, 52), Is.EqualTo(0x00040005u));
    }

    [Test]
    public void Build_ContainsExpectedStringKeys()
    {
        var blob = BatVersionResource.Build(MakeInfo());

        foreach (var key in new[]
                 {
                     "VS_VERSION_INFO", "StringFileInfo", "040904B0", "VarFileInfo", "Translation",
                     "FileDescription", "ProductName", "FileVersion", "OriginalFilename",
                 })
        {
            Assert.That(IndexOf(blob, Encoding.Unicode.GetBytes(key + "\0")),
                Is.GreaterThanOrEqualTo(0), key);
        }
    }

    [Test]
    public void Build_IncludesCompanyAndCopyrightOnlyWhenSet()
    {
        var without = BatVersionResource.Build(MakeInfo(company: "", copyright: ""));
        Assert.That(IndexOf(without, Encoding.Unicode.GetBytes("CompanyName\0")), Is.LessThan(0));
        Assert.That(IndexOf(without, Encoding.Unicode.GetBytes("LegalCopyright\0")), Is.LessThan(0));

        var with = BatVersionResource.Build(MakeInfo(company: "ACME Inc.", copyright: "(c) 2026"));
        Assert.That(IndexOf(with, Encoding.Unicode.GetBytes("CompanyName\0")), Is.GreaterThanOrEqualTo(0));
        Assert.That(IndexOf(with, Encoding.Unicode.GetBytes("LegalCopyright\0")), Is.GreaterThanOrEqualTo(0));
        Assert.That(IndexOf(with, Encoding.Unicode.GetBytes("ACME Inc.\0")), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Build_StringValueLength_IsWcharsIncludingNull()
    {
        const string title = "My Tool";
        var blob = BatVersionResource.Build(MakeInfo(title: title));

        var keyStart = IndexOf(blob, Encoding.Unicode.GetBytes("FileDescription\0"));
        Assert.That(keyStart, Is.GreaterThanOrEqualTo(6));
        Assert.That(BitConverter.ToUInt16(blob, keyStart - 4), Is.EqualTo((ushort)(title.Length + 1)),
            "wValueLength counts WCHARs including the terminator");
        Assert.That(BitConverter.ToUInt16(blob, keyStart - 2), Is.EqualTo(1),
            "string entries are wType=1");
    }

    [Test]
    public void Build_StringValueFollowsAlignedKey()
    {
        const string versionText = "2.3.4.5";
        var blob = BatVersionResource.Build(MakeInfo(fileVersion: versionText));

        var keyStart = IndexOf(blob, Encoding.Unicode.GetBytes("FileVersion\0"));
        Assert.That(keyStart, Is.GreaterThanOrEqualTo(0));

        var valueOffset = Align4(keyStart + Encoding.Unicode.GetByteCount("FileVersion\0"));
        Assert.That(IndexOf(blob, Encoding.Unicode.GetBytes(versionText + "\0"), valueOffset),
            Is.EqualTo(valueOffset));
    }

    [Test]
    public void Build_ContainsTranslationWithNeutralLanguage()
    {
        var blob = BatVersionResource.Build(MakeInfo());
        Assert.That(IndexOf(blob, new byte[] { 0x09, 0x04, 0xB0, 0x04 }), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Build_NullInfo_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BatVersionResource.Build(null!));
    }

    private static int Align4(int offset) => (offset + 3) & ~3;

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
