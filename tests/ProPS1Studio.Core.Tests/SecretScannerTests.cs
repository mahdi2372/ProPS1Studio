using NUnit.Framework;
using ProPS1Studio.Core.Security;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class SecretScannerTests
{
    private readonly SecretScanner _scanner = new();

    [Test]
    public void Scan_DetectsAwsAccessKey_AsHigh()
    {
        var result = _scanner.Scan("$key = 'AKIAIOSFODNN7EXAMPLE'");

        Assert.That(result.HasSecrets, Is.True);
        Assert.That(result.HighCount, Is.GreaterThanOrEqualTo(1));
        var finding = result.Findings.Single(f => f.RuleId == "aws-access-key");
        Assert.That(finding.Severity, Is.EqualTo(SecretSeverity.High));
        Assert.That(finding.LineNumber, Is.EqualTo(1));
        Assert.That(finding.MaskedValue, Does.Not.Contain("IOSFODNN7EXAMPLE"));
        Assert.That(finding.MaskedValue, Does.StartWith("AKIA"));
    }

    [Test]
    public void Scan_DetectsGitHubToken()
    {
        var result = _scanner.Scan("token=ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        Assert.That(result.Findings.Any(f => f.RuleId == "github-token"), Is.True);
    }

    [Test]
    public void Scan_DetectsSlackToken()
    {
        var result = _scanner.Scan("# slack: xoxb-1234567890-abcdef123456-abcdefghijklmnopqrstuvwx");
        Assert.That(result.Findings.Any(f => f.RuleId == "slack-token"), Is.True);
    }

    [Test]
    public void Scan_DetectsPrivateKeyBlock()
    {
        var result = _scanner.Scan("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----");
        Assert.That(result.Findings.Any(f => f.RuleId == "private-key"), Is.True);
        Assert.That(result.HighCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Scan_DetectsCredentialInUrl()
    {
        var result = _scanner.Scan("Invoke-RestMethod https://admin:S3cr3tP4ss@internal.example.com/api");
        Assert.That(result.Findings.Any(f => f.RuleId == "basic-auth-url"), Is.True);
    }

    [Test]
    public void Scan_DetectsJwt_AsMedium()
    {
        var result = _scanner.Scan("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpM");
        var jwt = result.Findings.SingleOrDefault(f => f.RuleId == "jwt");
        Assert.That(jwt, Is.Not.Null);
        Assert.That(jwt!.Severity, Is.EqualTo(SecretSeverity.Medium));
    }

    [Test]
    public void Scan_DetectsRealisticPasswordAssignment()
    {
        var result = _scanner.Scan("password = MyP@ssw0rd99x");
        var finding = result.Findings.SingleOrDefault(f => f.RuleId == "generic-secret");
        Assert.That(finding, Is.Not.Null, "expected a generic-secret finding");
        Assert.That(finding!.MaskedValue, Does.Not.Contain("MyP@ssw0rd99x"));
    }

    [Test]
    public void Scan_FiltersPlaceholdersAndVariables()
    {
        var clean = """
            $password = $env:DB_PASSWORD
            token = 'password'
            secret = "example-token"
            """;
        var result = _scanner.Scan(clean);
        Assert.That(result.HasSecrets, Is.False, string.Join("; ", result.Findings.Select(f => f.RuleId)));
    }

    [Test]
    public void Scan_CleanScript_HasNoFindings()
    {
        var result = _scanner.Scan("Write-Host 'hello world'\nGet-Date\n");
        Assert.That(result.HasSecrets, Is.False);
        Assert.That(result.HighCount, Is.Zero);
    }

    [Test]
    public void Scan_EmptyContent_HasNoFindings()
    {
        Assert.That(_scanner.Scan(string.Empty).HasSecrets, Is.False);
    }

    [Test]
    public void Scan_ReportsLineAndColumn()
    {
        var result = _scanner.Scan("Write-Host 1\nWrite-Host 2\n$key = 'AKIAIOSFODNN7EXAMPLE'");
        var finding = result.Findings.Single(f => f.RuleId == "aws-access-key");
        Assert.That(finding.LineNumber, Is.EqualTo(3));
        Assert.That(finding.Column, Is.GreaterThan(1));
    }

    [Test]
    public void Scan_ExtraPattern_Matches()
    {
        var result = _scanner.Scan("prefix: org-secret-987654 suffix", new[] { @"org-secret-\d+" });
        Assert.That(result.Findings.Any(f => f.RuleId == "custom"), Is.True);
    }

    [Test]
    public void Scan_InvalidExtraPattern_IsIgnoredNotFatal()
    {
        var result = _scanner.Scan("plain text", new[] { @"([" });
        Assert.That(result.HasSecrets, Is.False);
    }

    [Test]
    public void Mask_UsesPrefixAndSuffixForLongValues()
    {
        Assert.That(SecretScanner.Mask("abcdefghijkl"), Is.EqualTo("abcd…ijkl"));
    }

    [Test]
    public void Mask_FullyMasksShortValues()
    {
        var masked = SecretScanner.Mask("short");
        Assert.That(masked, Does.Not.Contain("short"));
        Assert.That(masked, Does.Contain("•"));
    }

    [Test]
    public void IsLikelyPlaceholder_ClassifiesCommonPatterns()
    {
        Assert.That(SecretScanner.IsLikelyPlaceholder("$env:TOKEN"), Is.True);
        Assert.That(SecretScanner.IsLikelyPlaceholder("%PATHTOKEN%"), Is.True);
        Assert.That(SecretScanner.IsLikelyPlaceholder("password"), Is.True);
        Assert.That(SecretScanner.IsLikelyPlaceholder("abcd"), Is.True);
        Assert.That(SecretScanner.IsLikelyPlaceholder("1234567890"), Is.True);
        Assert.That(SecretScanner.IsLikelyPlaceholder("MyP@ssw0rd99x"), Is.False);
    }
}
