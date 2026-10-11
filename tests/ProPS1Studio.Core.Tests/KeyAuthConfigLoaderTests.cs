using NUnit.Framework;
using ProPS1Studio.Core.Auth;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class KeyAuthConfigLoaderTests
{
    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"props1-keyauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // ignored
        }
    }

    private static Func<string, string?> NoEnv => _ => null;

    [Test]
    public void Load_NoFiles_ReturnsUnconfiguredDefaults()
    {
        var config = KeyAuthConfigLoader.Load(new[] { Path.Combine(_dir, "missing.json") }, NoEnv);

        Assert.That(config.Name, Is.Empty);
        Assert.That(config.IsConfigured, Is.False);
        Assert.That(config.Version, Is.EqualTo("1.0"));
        Assert.That(config.Endpoint, Is.EqualTo("https://keyauth.win/api/1.3/"));
    }

    [Test]
    public void Load_ValidFile_ParsesAndNormalizes()
    {
        var path = Path.Combine(_dir, "keyauth.json");
        File.WriteAllText(path, """
            {
              "name": "me",
              "ownerId": "dh97gCHBD8",
              "version": "1.0",
              "endpoint": "https://keyauth.win/api/1.3"
            }
            """);

        var config = KeyAuthConfigLoader.Load(new[] { path }, NoEnv);

        Assert.That(config.Name, Is.EqualTo("me"));
        Assert.That(config.OwnerId, Is.EqualTo("dh97gCHBD8"));
        Assert.That(config.IsConfigured, Is.True);
        Assert.That(config.Endpoint, Does.EndWith("/"), "endpoint is normalized with a trailing slash");
    }

    [Test]
    public void Load_IsCaseInsensitiveForPropertyNames()
    {
        var path = Path.Combine(_dir, "keyauth.json");
        File.WriteAllText(path, "{ \"NAME\": \"me\", \"ownerid\": \"dh97gCHBD8\" }");

        var config = KeyAuthConfigLoader.Load(new[] { path }, NoEnv);

        Assert.That(config.Name, Is.EqualTo("me"));
        Assert.That(config.IsConfigured, Is.True);
    }

    [Test]
    public void Load_MalformedFile_ReturnsDefaults()
    {
        var path = Path.Combine(_dir, "keyauth.json");
        File.WriteAllText(path, "{ this is not json !!!");

        var config = KeyAuthConfigLoader.Load(new[] { path }, NoEnv);

        Assert.That(config.IsConfigured, Is.False);
        Assert.That(config.Version, Is.EqualTo("1.0"));
    }

    [Test]
    public void Load_EnvironmentOverridesFile()
    {
        var path = Path.Combine(_dir, "keyauth.json");
        File.WriteAllText(path, "{ \"name\": \"fromfile\", \"ownerId\": \"dh97gCHBD8\" }");
        var env = new Dictionary<string, string?>
        {
            ["PROPS_KEYAUTH_NAME"] = "fromenv",
            ["PROPS_KEYAUTH_OWNERID"] = "envowner001",
            ["PROPS_KEYAUTH_VERSION"] = "2.0",
        };

        var config = KeyAuthConfigLoader.Load(
            new[] { path },
            key => env.TryGetValue(key, out var value) ? value : null);

        Assert.That(config.Name, Is.EqualTo("fromenv"));
        Assert.That(config.OwnerId, Is.EqualTo("envowner001"));
        Assert.That(config.Version, Is.EqualTo("2.0"));
    }

    [Test]
    public void Load_FirstExistingCandidateWins()
    {
        var first = Path.Combine(_dir, "first.json");
        var second = Path.Combine(_dir, "second.json");
        File.WriteAllText(first, "{ \"name\": \"first\", \"ownerId\": \"dh97gCHBD8\" }");
        File.WriteAllText(second, "{ \"name\": \"second\", \"ownerId\": \"dh97gCHBD8\" }");

        var config = KeyAuthConfigLoader.Load(new[] { Path.Combine(_dir, "nope.json"), first, second }, NoEnv);

        Assert.That(config.Name, Is.EqualTo("first"));
    }

    [Test]
    public void Load_EmptyEnvValues_AreIgnored()
    {
        var path = Path.Combine(_dir, "keyauth.json");
        File.WriteAllText(path, "{ \"name\": \"fromfile\", \"ownerId\": \"dh97gCHBD8\" }");

        var config = KeyAuthConfigLoader.Load(
            new[] { path },
            key => key == "PROPS_KEYAUTH_NAME" ? "   " : null);

        Assert.That(config.Name, Is.EqualTo("fromfile"), "blank env vars must not wipe the file");
    }
}
