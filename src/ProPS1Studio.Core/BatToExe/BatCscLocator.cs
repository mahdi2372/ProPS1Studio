namespace ProPS1Studio.Core.BatToExe;

/// <summary>
/// Locates the .NET Framework C# compiler (csc.exe) used to build the batch launcher.
/// Order: explicit override, the PROPS_CSC_PATH environment variable, then the in-box
/// Framework64/Framework v4.0.30319 installs. Returns null when nothing usable is found;
/// a configured override that points at a missing file fails fast (null) instead of
/// silently falling back to a different compiler.
/// </summary>
public static class BatCscLocator
{
    public const string OverrideEnvironmentVariable = "PROPS_CSC_PATH";

    public static string? Find(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return File.Exists(fromEnvironment) ? fromEnvironment : null;
        }

        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (string.IsNullOrWhiteSpace(windir))
        {
            windir = @"C:\Windows";
        }

        string[] candidates =
        {
            Path.Combine(windir, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
            Path.Combine(windir, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
