namespace ProPS1Studio.Core.Settings;

/// <summary>Expands the output-path template into a concrete file name.
/// Tokens: {name} input file name, {ext} input extension, {date} yyyyMMdd, {time} HHmmss.
/// An empty or malformed template falls back to "{name}.exe".</summary>
public static class OutputPathTemplate
{
    public const string Default = "{name}.exe";

    public static string Expand(string? template, string inputPath)
    {
        var name = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "output";
        }

        var ext = Path.GetExtension(inputPath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = string.Empty;
        }

        var effective = string.IsNullOrWhiteSpace(template) ? Default : template!;
        var expanded = effective
            .Replace("{name}", name)
            .Replace("{ext}", ext.TrimStart('.'))
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd"))
            .Replace("{time}", DateTime.Now.ToString("HHmmss"));

        // No {name} token: append the name so the output is never an empty file name.
        if (!effective.Contains("{name}") && !expanded.EndsWith(name, StringComparison.Ordinal))
        {
            expanded = string.IsNullOrWhiteSpace(expanded)
                ? name + ".exe"
                : expanded + "-" + name;
        }

        return expanded;
    }
}
