using System.Xml.Linq;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class BatManifestBuilderTests
{
    [Test]
    public void Build_IsWellFormedXml()
    {
        foreach (var level in Enum.GetValues<BatExecutionLevel>())
        {
            var xml = BatManifestBuilder.Build(level);
            Assert.DoesNotThrow(() => XDocument.Parse(xml), level.ToString());
        }
    }

    [Test]
    public void Build_ContainsExactlyOneExecutionLevel()
    {
        foreach (var level in Enum.GetValues<BatExecutionLevel>())
        {
            var xml = BatManifestBuilder.Build(level);
            foreach (var candidate in new[] { "asInvoker", "requireAdministrator", "highestAvailable" })
            {
                var expected = candidate == BatManifestBuilder.LevelString(level) ? 1 : 0;
                var actual = CountOccurrences(xml, candidate);
                Assert.That(actual, Is.EqualTo(expected), $"{level} vs {candidate}");
            }
        }
    }

    [Test]
    public void Build_KeepsLauncherIdentityAndWindowsSettings()
    {
        var xml = BatManifestBuilder.Build(BatExecutionLevel.AsInvoker);
        Assert.That(xml, Does.Contain("ProPS1Studio.BatLauncher"));
        Assert.That(xml, Does.Contain("PerMonitorV2"));
        Assert.That(xml, Does.Contain("longPathAware"));
        Assert.That(xml, Does.Contain("true/pm"));
        Assert.That(CountOccurrences(xml, "<supportedOS "), Is.EqualTo(5));
    }

    [TestCase(BatExecutionLevel.AsInvoker, "asInvoker")]
    [TestCase(BatExecutionLevel.RequireAdministrator, "requireAdministrator")]
    [TestCase(BatExecutionLevel.HighestAvailable, "highestAvailable")]
    public void LevelString_And_ParseLevel_RoundTrip(BatExecutionLevel level, string text)
    {
        Assert.That(BatManifestBuilder.LevelString(level), Is.EqualTo(text));
        Assert.That(BatManifestBuilder.ParseLevel(text), Is.EqualTo(level));
    }

    [Test]
    public void ParseLevel_UnknownValue_FallsBackToAsInvoker()
    {
        Assert.That(BatManifestBuilder.ParseLevel("bogus"), Is.EqualTo(BatExecutionLevel.AsInvoker));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
