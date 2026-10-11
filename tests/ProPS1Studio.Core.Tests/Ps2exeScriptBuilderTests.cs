using NUnit.Framework;
using ProPS1Studio.Core.Conversion;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class Ps2exeScriptBuilderTests
{
    private static readonly Ps2exeCapability FullCapability = new()
    {
        IsAvailable = true,
        ModuleVersion = "1.0.18",
        Parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "in", "outputFile", "requireAdmin", "noConsole", "iconFile",
            "title", "description", "company", "product", "copyright", "version", "x86", "x64",
        },
    };

    private static ConversionRequest BaseRequest => new()
    {
        InputPath = @"C:\scripts\app.ps1",
        OutputPath = @"C:\builds\app.exe",
    };

    [Test]
    public void Build_EmitsEscapedInputsAndInvocation()
    {
        var script = Ps2exeScriptBuilder.Build(
            new ConversionRequest
            {
                InputPath = @"C:\scripts\app.ps1",
                OutputPath = @"C:\builds\app.exe",
                Title = "It's Mine",
            },
            FullCapability);

        Assert.That(script, Does.Contain("Import-Module ps2exe -Force"));
        Assert.That(script, Does.Contain("in = 'C:\\scripts\\app.ps1'"));
        Assert.That(script, Does.Contain("outputFile = 'C:\\builds\\app.exe'"));
        Assert.That(script, Does.Contain("title = 'It''s Mine'"));
        Assert.That(script, Does.Contain("Invoke-ps2exe @splat"));
        Assert.That(script, Does.Contain("exit 0"));
    }

    [Test]
    public void Build_EmitsFlagsWhenEnabled()
    {
        var script = Ps2exeScriptBuilder.Build(
            new ConversionRequest
            {
                InputPath = @"C:\scripts\app.ps1",
                OutputPath = @"C:\builds\app.exe",
                RequireAdministrator = true,
                NoConsole = true,
                Description = "desc",
                Company = "co",
                Product = "prod",
                Copyright = "(c)",
                FileVersion = "2.1.0",
            },
            FullCapability);

        Assert.That(script, Does.Contain("requireAdmin = $true"));
        Assert.That(script, Does.Contain("noConsole = $true"));
        Assert.That(script, Does.Contain("description = 'desc'"));
        Assert.That(script, Does.Contain("company = 'co'"));
        Assert.That(script, Does.Contain("product = 'prod'"));
        Assert.That(script, Does.Contain("copyright = '(c)'"));
        Assert.That(script, Does.Contain("version = '2.1.0'"));
    }

    [Test]
    public void Build_OmitsDisabledOptions()
    {
        var script = Ps2exeScriptBuilder.Build(BaseRequest, FullCapability);

        Assert.That(script, Does.Not.Contain("requireAdmin"));
        Assert.That(script, Does.Not.Contain("noConsole"));
        Assert.That(script, Does.Not.Contain("title"));
        Assert.That(script, Does.Not.Contain("version"));
    }

    [Test]
    public void Build_UsesExplicitModulePathWhenProvided()
    {
        var script = Ps2exeScriptBuilder.Build(
            new ConversionRequest
            {
                InputPath = @"C:\scripts\app.ps1",
                OutputPath = @"C:\builds\app.exe",
                Ps2exeModulePath = @"C:\mods\ps2exe\ps2exe.psd1",
            },
            FullCapability);

        Assert.That(script, Does.Contain("Import-Module -Name 'C:\\mods\\ps2exe\\ps2exe.psd1' -Force"));
        Assert.That(script, Does.Not.Contain("Import-Module ps2exe -Force"));
    }

    [Test]
    public void Build_UnsupportedParameter_ThrowsNotSupported()
    {
        var capability = new Ps2exeCapability
        {
            IsAvailable = true,
            Parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "in", "outputFile" },
        };

        var ex = Assert.Throws<NotSupportedException>(() =>
            Ps2exeScriptBuilder.Build(
                new ConversionRequest
                {
                    InputPath = @"C:\scripts\app.ps1",
                    OutputPath = @"C:\builds\app.exe",
                    RequireAdministrator = true,
                },
                capability));
        Assert.That(ex!.Message, Does.Contain("requireAdmin"));
    }

    [Test]
    public void Build_UnavailableModule_ThrowsInvalidOperation()
    {
        var unavailable = Ps2exeCapability.Unavailable("not installed");
        Assert.Throws<InvalidOperationException>(() =>
            Ps2exeScriptBuilder.Build(BaseRequest, unavailable));
    }

    [Test]
    public void Build_UsesInputOverrideWhenProvided()
    {
        var script = Ps2exeScriptBuilder.Build(BaseRequest, FullCapability,
            inputOverride: @"C:\temp\encrypt-loader.ps1");

        Assert.That(script, Does.Contain("in = 'C:\\temp\\encrypt-loader.ps1'"));
        Assert.That(script, Does.Not.Contain("app.ps1"));
    }

    [Test]
    public void Build_FallsBackToRequestInputWithoutOverride()
    {
        var script = Ps2exeScriptBuilder.Build(BaseRequest, FullCapability);

        Assert.That(script, Does.Contain("in = 'C:\\scripts\\app.ps1'"));
    }

    [TestCase("simple", "'simple'")]
    [TestCase("it's", "'it''s'")]
    [TestCase("a'b'c", "'a''b''c'")]
    [TestCase("", "''")]
    public void Q_EscapesSingleQuotes(string input, string expected)
    {
        Assert.That(Ps2exeScriptBuilder.Q(input), Is.EqualTo(expected));
    }

    [Test]
    public void Supports_IsCaseInsensitive()
    {
        Assert.That(FullCapability.Supports("REQUIREADMIN"), Is.True);
        Assert.That(FullCapability.Supports("nope"), Is.False);
    }
}
