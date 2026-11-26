using NUnit.Framework;
using ProPS1Studio.Core.Utilities;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class OptionSetTests
{
    [Test]
    public void Parse_CollectsPositionalsInOrder()
    {
        var set = OptionSet.Parse(new[] { "convert", "script.ps1" });
        Assert.That(set.Positionals, Is.EqualTo(new[] { "convert", "script.ps1" }));
    }

    [Test]
    public void Parse_FlagIsDetected()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--admin", "--verbose" });
        Assert.That(set.GetBool("admin"), Is.True);
        Assert.That(set.GetBool("verbose"), Is.True);
        Assert.That(set.Positionals, Is.EqualTo(new[] { "script.ps1" }));
    }

    [Test]
    public void Parse_ValueOptionWithSpace()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--output", "out.exe" });
        Assert.That(set.GetString("output"), Is.EqualTo("out.exe"));
    }

    [Test]
    public void Parse_ValueOptionWithEquals()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--output=out.exe" });
        Assert.That(set.GetString("output"), Is.EqualTo("out.exe"));
    }

    [Test]
    public void Parse_ShortValueOption()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "-o", "out.exe" });
        Assert.That(set.GetString("o"), Is.EqualTo("out.exe"));
    }

    [Test]
    public void GetBoolOrDefault_NegationFlag()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--no-encrypt" });
        Assert.That(set.GetBoolOrDefault("encrypt", true), Is.False);
    }

    [Test]
    public void GetBoolOrDefault_PositiveFlag()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--encrypt" });
        Assert.That(set.GetBoolOrDefault("encrypt", false), Is.True);
    }

    [Test]
    public void GetBoolOrDefault_FallbackWhenAbsent()
    {
        var set = OptionSet.Parse(new[] { "script.ps1" });
        Assert.That(set.GetBoolOrDefault("encrypt", true), Is.True);
        Assert.That(set.GetBoolOrDefault("protect", false), Is.False);
    }

    [Test]
    public void GetIntOrDefault_ParsesAndFallsBack()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--timeout", "90" });
        Assert.That(set.GetIntOrDefault("timeout", 180), Is.EqualTo(90));

        var defaultSet = OptionSet.Parse(new[] { "script.ps1" });
        Assert.That(defaultSet.GetIntOrDefault("timeout", 180), Is.EqualTo(180));
    }

    [Test]
    public void GetIntOrDefault_InvalidValueThrows()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--timeout", "abc" });
        Assert.Throws<OptionParseException>(() => set.GetIntOrDefault("timeout", 180));
    }

    [Test]
    public void GetList_CollectsRepeatableValues()
    {
        var set = OptionSet.Parse(new[]
        {
            "file.ps1",
            "--extra-pattern", "org-secret-\\d+",
            "--extra-pattern", "internal-[A-Z]+",
        });
        Assert.That(set.GetList("extra-pattern"), Is.EqualTo(new[] { "org-secret-\\d+", "internal-[A-Z]+" }));
    }

    [Test]
    public void Parse_EndOfOptionsTreatsRestAsPositionals()
    {
        var set = OptionSet.Parse(new[] { "script.ps1", "--", "--not-a-flag" });
        Assert.That(set.Positionals, Is.EqualTo(new[] { "script.ps1", "--not-a-flag" }));
    }

    [Test]
    public void Parse_BareDashIsPositional()
    {
        var set = OptionSet.Parse(new[] { "-" });
        Assert.That(set.Positionals, Is.EqualTo(new[] { "-" }));
    }

    [Test]
    public void Parse_ValueAfterFlagIsNotSwallowedByNextFlag()
    {
        // A value must not be consumed when the next token is another flag.
        var set = OptionSet.Parse(new[] { "script.ps1", "--title", "--admin" });
        Assert.That(set.GetBool("admin"), Is.True);
        Assert.That(set.GetString("title"), Is.Null);
    }
}
