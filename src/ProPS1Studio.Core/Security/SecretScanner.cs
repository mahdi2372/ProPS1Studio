using System.Text.RegularExpressions;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.Core.Security;

public enum SecretSeverity
{
    Low,
    Medium,
    High,
}

public sealed record SecretFinding(
    string RuleId,
    string RuleName,
    int LineNumber,
    int Column,
    string MaskedValue,
    SecretSeverity Severity);

public sealed class SecretScanResult
{
    public IReadOnlyList<SecretFinding> Findings { get; init; } = Array.Empty<SecretFinding>();
    public bool HasSecrets => Findings.Count > 0;
    public int HighCount => Findings.Count(f => f.Severity == SecretSeverity.High);
}

public interface ISecretScanner
{
    SecretScanResult Scan(string content, IReadOnlyList<string>? extraPatterns = null);
}

/// <summary>
/// Detects hardcoded credentials in scripts before they get baked into an executable.
/// Masked output only: raw matches are never returned or logged.
/// </summary>
public sealed class SecretScanner : ISecretScanner
{
    private sealed record Rule(
        string Id,
        string Name,
        Regex Pattern,
        SecretSeverity Severity,
        bool ApplyHeuristicFilter = false);

    private static readonly Rule[] BuiltInRules =
    {
        new("aws-access-key", "AWS access key ID",
            new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("github-token", "GitHub token",
            new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("slack-token", "Slack token",
            new Regex(@"\bxox[baprs]-[0-9A-Za-z-]{10,}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("google-api-key", "Google API key",
            new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("private-key", "Private key block",
            new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----",
                RegexOptions.Compiled), SecretSeverity.High),
        new("azure-key", "Azure storage account key / SAS",
            new Regex(@"(?i)\b(?:AccountKey|SharedAccessSignature)=([A-Za-z0-9+/=]{16,})",
                RegexOptions.Compiled), SecretSeverity.High),
        new("basic-auth-url", "Credential embedded in URL",
            new Regex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^/\s:@]+:[^/\s:@]{4,}@", RegexOptions.Compiled),
            SecretSeverity.High),
        new("jwt", "JSON Web Token",
            new Regex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b",
                RegexOptions.Compiled), SecretSeverity.Medium),
        new("generic-secret", "Hardcoded credential assignment",
            new Regex(@"(?i)\b(?:password|passwd|pwd|secret|token|api[_\-]?key|access[_\-]?key)\s*[:=]\s*['""]?(?<value>[^\s'"";,\)]{6,})",
                RegexOptions.Compiled), SecretSeverity.Medium, ApplyHeuristicFilter: true),
        new("connection-string-password", "Connection string password",
            new Regex(@"(?i)\b(?:Password|Pwd)\s*=\s*(?<value>[^;'\s""`]{6,})", RegexOptions.Compiled),
            SecretSeverity.Medium, ApplyHeuristicFilter: true),
    };

    /// <summary>Values that are obviously placeholders, references, or documentation samples.</summary>
    private static readonly Regex PlaceholderPattern = new(
        @"^(?:" +
        @"x{3,}|\*{3,}|\-{3,}|_{3,}|\.{3,}|" +
        @"your[\w\-\.]*|my[\w\-\.]*|change[\w\-\.]*|example[\w\-\.]*|placeholder[\w\-\.]*|" +
        @"dummy|sample|test|testing|fake|demo|secret|password|admin|123456\w*|" +
        @"<[^>]*>|\$\{[^}]*\}|\$env:.*|%[\w]+%|" +
        @"null|none|true|false|nil|todo|fixme|redacted|\[redacted\]" +
        @")$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SecretScanResult Scan(string content, IReadOnlyList<string>? extraPatterns = null)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new SecretScanResult();
        }

        var findings = new List<SecretFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in BuiltInRules)
        {
            Collect(content, rule, findings, seen);
        }

        if (extraPatterns is { Count: > 0 })
        {
            foreach (var pattern in extraPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                try
                {
                    var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    Collect(content,
                        new Rule("custom", "Custom pattern", regex, SecretSeverity.Medium, ApplyHeuristicFilter: true),
                        findings, seen);
                }
                catch (ArgumentException)
                {
                    // Invalid user pattern: skip rather than failing the scan.
                }
            }
        }

        findings.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
        return new SecretScanResult { Findings = findings };
    }

    private static void Collect(string content, Rule rule, List<SecretFinding> findings, HashSet<string> seen)
    {
        foreach (Match match in rule.Pattern.Matches(content))
        {
            var value = match.Groups["value"].Success
                ? match.Groups["value"].Value
                : match.Value;

            if (rule.ApplyHeuristicFilter && IsLikelyPlaceholder(value))
            {
                continue;
            }

            var (line, column) = LineColumnOf(content, match.Index);
            var key = $"{rule.Id}:{line}:{value}";
            if (!seen.Add(key))
            {
                continue;
            }

            findings.Add(new SecretFinding(
                rule.Id, rule.Name, line, column, Mask(value), rule.Severity));
        }
    }

    /// <summary>True when the matched value is a placeholder, variable reference, or trivial string.</summary>
    public static bool IsLikelyPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 6)
        {
            return true;
        }

        if (trimmed.StartsWith('$') || trimmed.StartsWith('%') || trimmed.StartsWith('<') ||
            trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith("Invoke-") ||
            trimmed.StartsWith("Get-") || trimmed.StartsWith("New-") || trimmed.StartsWith("$env"))
        {
            return true;
        }

        if (PlaceholderPattern.IsMatch(trimmed))
        {
            return true;
        }

        // Values built only from one character class (aaaaaa, 123456, ....) are low signal.
        if (trimmed.All(char.IsDigit) || trimmed.All(c => c == 'a') || trimmed.Distinct().Count() <= 2)
        {
            return true;
        }

        // Anything that looks like code (spaces around operators, parentheses) is not a literal secret.
        if (trimmed.Contains('(') || trimmed.Contains(' ') || trimmed.Contains('+'))
        {
            return true;
        }

        return false;
    }

    public static string Mask(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= 8)
        {
            return new string('•', Math.Max(4, trimmed.Length));
        }

        return $"{trimmed[..4]}…{trimmed[^4..]}";
    }

    private static (int Line, int Column) LineColumnOf(string content, int index)
    {
        var line = 1;
        var lastLineStart = 0;
        for (var i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
                lastLineStart = i + 1;
            }
        }

        return (line, index - lastLineStart + 1);
    }
}
