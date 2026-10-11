using NUnit.Framework;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class LogSanitizerTests
{
    [Test]
    public void Sanitize_RedactsAwsAccessKey()
    {
        var key = "AKIAIOSFODNN7EXAMPLE";
        var sanitized = LogSanitizer.Sanitize($"using key {key} now");

        Assert.That(sanitized, Does.Not.Contain(key));
        Assert.That(sanitized, Does.Contain("[REDACTED]"));
    }

    [Test]
    public void Sanitize_RedactsPasswordAssignment()
    {
        var sanitized = LogSanitizer.Sanitize("connecting with password = Sup3rSecret99x");

        Assert.That(sanitized, Does.Not.Contain("Sup3rSecret99x"));
        Assert.That(sanitized, Does.Contain("[REDACTED]"));
    }

    [Test]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string pem =
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEAx\n-----END RSA PRIVATE KEY-----";
        var sanitized = LogSanitizer.Sanitize($"payload: {pem} end");

        Assert.That(sanitized, Does.Not.Contain("MIIEpAIBAAKCAQEAx"));
        Assert.That(sanitized, Does.Contain("[REDACTED]"));
    }

    [Test]
    public void Sanitize_LeavesPlainMessagesUntouched()
    {
        const string message = "Conversion finished in 1.8 s, output is 42 KB.";
        Assert.That(LogSanitizer.Sanitize(message), Is.EqualTo(message));
    }

    [Test]
    public void Sanitize_RedactsBearerToken()
    {
        var sanitized = LogSanitizer.Sanitize("Authorization: Bearer abcdefghijklmnop1234567890");
        Assert.That(sanitized, Does.Not.Contain("abcdefghijklmnop1234567890"));
        Assert.That(sanitized, Does.Contain("[REDACTED]"));
    }

    [Test]
    public void Sanitize_HandlesEmptyAndNullishInput()
    {
        Assert.That(LogSanitizer.Sanitize(string.Empty), Is.Empty);
    }
}
