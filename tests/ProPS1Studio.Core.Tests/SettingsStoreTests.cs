using NUnit.Framework;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class SettingsStoreTests
{
    private string _dir = string.Empty;
    private string _path = string.Empty;
    private AppLogger? _logger;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"props1-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
        _logger = new AppLogger(Path.Combine(_dir, "logs"));
    }

    [TearDown]
    public void TearDown()
    {
        _logger?.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // ignored
        }
    }

    private SettingsStore CreateStore() => new(_path, _logger!);

    [Test]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = CreateStore().Load();
        Assert.That(settings.EnableSecretScan, Is.True);
        Assert.That(settings.EncryptScript, Is.True, "encryption is on by default");
        Assert.That(settings.ConversionTimeoutSeconds, Is.EqualTo(180));
        Assert.That(settings.RecentConversions, Is.Empty);
    }

    [Test]
    public void SaveAndLoad_RoundTripsValues()
    {
        var store = CreateStore();
        var settings = new AppSettings
        {
            DefaultOutputDirectory = @"C:\builds",
            CompressWithUpx = true,
            UpxForce = true,
            EncryptScript = true,
            ConversionTimeoutSeconds = 420,
            SignCertificateThumbprint = "ABCDEF0123456789",
            SignAfterConversion = true,
            TimestampServer = "http://timestamp.example.com",
            ExtraSecretPatterns = { "custom-\\d+" },
        };
        settings.RecentConversions.Add(new RecentConversion
        {
            Timestamp = new DateTime(2026, 1, 2, 3, 4, 5),
            InputPath = @"C:\in\a.ps1",
            OutputPath = @"C:\out\a.exe",
            Success = true,
            OutputSizeBytes = 12345,
            RequireAdministrator = true,
            CompressedWithUpx = true,
        });

        store.Save(settings);
        var loaded = CreateStore().Load();

        Assert.That(loaded.DefaultOutputDirectory, Is.EqualTo(@"C:\builds"));
        Assert.That(loaded.CompressWithUpx, Is.True);
        Assert.That(loaded.UpxForce, Is.True);
        Assert.That(loaded.EncryptScript, Is.True);
        Assert.That(loaded.ConversionTimeoutSeconds, Is.EqualTo(420));
        Assert.That(loaded.SignCertificateThumbprint, Is.EqualTo("ABCDEF0123456789"));
        Assert.That(loaded.SignAfterConversion, Is.True);
        Assert.That(loaded.ExtraSecretPatterns, Does.Contain("custom-\\d+"));
        Assert.That(loaded.RecentConversions, Has.Count.EqualTo(1));
        Assert.That(loaded.RecentConversions[0].RequireAdministrator, Is.True);
        Assert.That(loaded.RecentConversions[0].OutputSizeBytes, Is.EqualTo(12345));
    }

    [Test]
    public void Load_CorruptFile_ResetsAndKeepsBackup()
    {
        File.WriteAllText(_path, "{ this is not valid json !!!");
        var store = CreateStore();
        var settings = store.Load();

        Assert.That(settings.ConversionTimeoutSeconds, Is.EqualTo(180), "defaults restored");
        Assert.That(File.Exists(_path + ".bak"), Is.True, "corrupt file kept as .bak");
        Assert.That(File.ReadAllText(_path), Does.Contain("conversionTimeoutSeconds"),
            "a valid settings file was rewritten");
    }

    [Test]
    public void Load_ClampsOutOfRangeTimeout()
    {
        File.WriteAllText(_path, "{ \"conversionTimeoutSeconds\": 5, \"enableSecretScan\": false }");
        var tooLow = CreateStore().Load();
        Assert.That(tooLow.ConversionTimeoutSeconds, Is.EqualTo(15));
        Assert.That(tooLow.EnableSecretScan, Is.False, "other values still load");

        File.WriteAllText(_path, "{ \"conversionTimeoutSeconds\": 99999 }");
        var tooHigh = CreateStore().Load();
        Assert.That(tooHigh.ConversionTimeoutSeconds, Is.EqualTo(3600));
    }

    [Test]
    public void Load_TrimsRecentConversionsBeyondLimit()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 80; i++)
        {
            settings.RecentConversions.Add(new RecentConversion
            {
                Timestamp = DateTime.Now,
                InputPath = $"in-{i}.ps1",
                Success = true,
            });
        }

        CreateStore().Save(settings);
        var loaded = CreateStore().Load();
        Assert.That(loaded.RecentConversions, Has.Count.EqualTo(50));
    }

    [Test]
    public void Load_HandlesJsonWithCommentsAndTrailingCommas()
    {
        File.WriteAllText(_path, """
            {
              // user edited this by hand
              "conversionTimeoutSeconds": 300,
            }
            """);
        var loaded = CreateStore().Load();
        Assert.That(loaded.ConversionTimeoutSeconds, Is.EqualTo(300));
    }

    [Test]
    public void Clone_ProducesIndependentCopy()
    {
        var original = new AppSettings { DefaultOutputDirectory = @"C:\one" };
        var clone = original.Clone();
        clone.DefaultOutputDirectory = @"C:\two";
        Assert.That(original.DefaultOutputDirectory, Is.EqualTo(@"C:\one"));
        Assert.That(clone.DefaultOutputDirectory, Is.EqualTo(@"C:\two"));
    }
}
