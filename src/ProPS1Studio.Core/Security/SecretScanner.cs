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
        new("github-pat", "GitHub fine-grained PAT",
            new Regex(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("gitlab-token", "GitLab token",
            new Regex(@"\bglpat-[A-Za-z0-9_\-]{20}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("stripe-secret", "Stripe secret key",
            new Regex(@"\b(?:sk|rk)_(?:live|test)_[0-9a-zA-Z]{16,}\b", RegexOptions.Compiled),
            SecretSeverity.High),
        new("twilio-api-key", "Twilio API key",
            new Regex(@"\bSK[0-9a-fA-F]{32}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("discord-bot-token", "Discord bot token",
            new Regex(@"\b[A-Za-z0-9_\-]{24}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27}\b",
                RegexOptions.Compiled), SecretSeverity.Medium),
        new("npm-token", "npm access token",
            new Regex(@"\bnpm_[A-Za-z0-9]{36}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("pypi-token", "PyPI token",
            new Regex(@"\bpypi-[A-Za-z0-9_\-]{30,}\b", RegexOptions.Compiled), SecretSeverity.Medium),
        new("google-oauth", "Google OAuth access token",
            new Regex(@"\bya29\.[A-Za-z0-9_\-]{16,}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("facebook-token", "Facebook access token",
            new Regex(@"\bEAA[A-Za-z0-9]{10,}\b", RegexOptions.Compiled), SecretSeverity.Medium),
        new("telegram-bot-token", "Telegram bot token",
            new Regex(@"\b[0-9]{8,10}:[A-Za-z0-9_\-]{35}\b", RegexOptions.Compiled),
            SecretSeverity.High),
        new("digitalocean-token", "DigitalOcean API token",
            new Regex(@"\bdop_v1_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("mailgun-key", "Mailgun API key",
            new Regex(@"\bkey-[A-Za-z0-9]{32}\b", RegexOptions.Compiled), SecretSeverity.High),
        new("sendgrid-key", "SendGrid API key",
            new Regex(@"\bSG\.[A-Za-z0-9_\-]{22}\.[A-Za-z0-9_\-]{43}\b", RegexOptions.Compiled),
            SecretSeverity.High),
        new("aws-secret-key", "AWS secret access key",
            new Regex(@"(?i)\baws_secret_access_key\s*[:=]\s*['\"]?(?<value>[A-Za-z0-9/+=]{40})",
                RegexOptions.Compiled), SecretSeverity.High),
        new("connection-string", "Connection string with credentials",
            new Regex(@"(?i)\b(?:Server|Data Source)\s*=\s*[^;]+;\s*(?:Password|Pwd)\s*=\s*(?<value>[^;'\s""`]{6,})",
                RegexOptions.Compiled), SecretSeverity.Medium, ApplyHeuristicFilter: true),
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
        var lineStarts = ComputeLineStarts(content);

        foreach (var rule in BuiltInRules)
        {
            Collect(content, rule, findings, seen, lineStarts);
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
                        findings, seen, lineStarts);
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

    private static void Collect(
        string content, Rule rule, List<SecretFinding> findings, HashSet<string> seen,
        List<int> lineStarts)
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

            var (line, column) = LineColumnOf(lineStarts, match.Index);
            var key = $"{rule.Id}:{line}:{value}";
            if (!seen.Add(key))
            {
                continue;
            }

            var severity = rule.Severity;

            // Credential assignments holding a long, high-entropy value are almost certainly
            // real secrets: boost them from Medium to High so they get the strictest policy.
            if (rule.Id is "generic-secret" or "connection-string" or "connection-string-password"
                && value.Length >= 16 && ShannonEntropy(value) >= 4.0)
            {
                severity = SecretSeverity.High;
            }

            findings.Add(new SecretFinding(
                rule.Id, rule.Name, line, column, Mask(value), severity));
        }
    }

    /// <summary>Shannon entropy (bits per character) of a value; high values suggest randomness.</summary>
    public static double ShannonEntropy(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var frequencies = new Dictionary<char, int>();
        foreach (var ch in value)
        {
            frequencies[ch] = frequencies.TryGetValue(ch, out var count) ? count + 1 : 1;
        }

        double entropy = 0;
        foreach (var count in frequencies.Values)
        {
            var p = (double)count / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>Serializes a scan result as JSON (findings are already masked) for reports and CI.</summary>
    public static string ToJson(SecretScanResult result)
    {
        var sb = new StringBuilder();
        sb.Append("{\"findings\":[");
        for (var i = 0; i < result.Findings.Count; i++)
        {
            var f = result.Findings[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"rule\":\"").Append(JsonEscape(f.RuleId))
              .Append("\",\"ruleName\":\"").Append(JsonEscape(f.RuleName))
              .Append("\",\"line\":").Append(f.LineNumber)
              .Append(",\"column\":").Append(f.Column)
              .Append(",\"severity\":\"").Append(f.Severity)
              .Append("\",\"maskedValue\":\"").Append(JsonEscape(f.MaskedValue))
              .Append("\"}");
        }

        sb.Append("],\"count\":").Append(result.Findings.Count)
          .Append(",\"highCount\":").Append(result.HighCount)
          .Append('}');
        return sb.ToString();
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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

        // Anything that looks like code (spaces around operators, parentheses) is not a literal
        // secret. '+' is deliberately NOT treated as code: base64 credentials (Azure storage
        // keys, API keys) legitimately contain '+', and suppressing them would hide real secrets.
        if (trimmed.Contains('(') || trimmed.Contains(' '))
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

    /// <summary>One pass over the content collecting every line-start offset; keeps the
    /// per-match line/column lookup at O(log n) instead of re-scanning from the start.</summary>
    private static List<int> ComputeLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>Locates the line and column of <paramref name="index"/> via binary search over
    /// precomputed line-start offsets.</summary>
    private static (int Line, int Column) LineColumnOf(List<int> lineStarts, int index)
    {
        var lo = 0;
        var hi = lineStarts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= index)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo + 1, index - lineStarts[lo] + 1);
    }
}
