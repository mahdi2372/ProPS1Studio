using System.Security.Cryptography;

namespace ProPS1Studio.Core.Processes;

/// <summary>Locates executables on the system PATH (pure managed, no shell involved).</summary>
public static class ToolLocator
{
    public static string? FindOnPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                if (!executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var withExe = candidate + ".exe";
                    if (File.Exists(withExe))
                    {
                        return withExe;
                    }
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    /// <summary>Computes the SHA-256 hash of a file, upper-case hex.</summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>True when the file's hash matches any of the pinned values (case-insensitive).</summary>
    public static bool MatchesAnySha256(string filePath, IEnumerable<string> expectedHashes)
    {
        string actual;
        try
        {
            actual = ComputeSha256(filePath);
        }
        catch
        {
            return false;
        }

        foreach (var expected in expectedHashes)
        {
            var candidate = expected.Trim();
            if (candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate["sha256:".Length..];
            }

            if (string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
