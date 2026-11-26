namespace ProPS1Studio.Core.Utilities;

/// <summary>Raised when command-line arguments cannot be parsed.</summary>
public sealed class OptionParseException : Exception
{
    public OptionParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Minimal shell-style option parser used by the CLI: positionals, boolean flags
/// (--flag), negated flags (--no-flag), value options (--opt value, --opt=value,
/// -o value) and repeatable options (--extra-pattern re).
/// </summary>
public sealed class OptionSet
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = new();
    private bool _endOfOptions;

    public IReadOnlyList<string> Positionals => _positionals;

    public static OptionSet Parse(string[] args)
    {
        var set = new OptionSet();
        var i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (set._endOfOptions || !arg.StartsWith('-') || arg == "-")
            {
                set._positionals.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                set._endOfOptions = true;
                i++;
                continue;
            }

            var name = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg[1..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }

            if (value is null && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                value = args[++i];
            }

            if (value is null)
            {
                set._flags.Add(name);
            }
            else
            {
                if (!set._values.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    set._values[name] = list;
                }

                list.Add(value);
            }

            i++;
        }

        return set;
    }

    public bool GetBool(string name) =>
        _flags.Contains(name) ||
        (_values.TryGetValue(name, out var list) &&
         list.Any(v => v is "1" or "true" or "yes"));

    /// <summary>--flag forces true, --no-flag forces false, otherwise the fallback.</summary>
    public bool GetBoolOrDefault(string name, bool fallback)
    {
        if (_flags.Contains(name))
        {
            return true;
        }

        if (_values.TryGetValue("no-" + name, out var list))
        {
            return !list.Any(v => v is "1" or "true" or "yes");
        }

        return fallback;
    }

    public string? GetString(string name) =>
        _values.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;

    public int GetIntOrDefault(string name, int fallback)
    {
        var raw = GetString(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw, out var value))
        {
            return value;
        }

        throw new OptionParseException($"Option '--{name}' expects an integer, got '{raw}'.");
    }

    public IReadOnlyList<string> GetList(string name) =>
        _values.TryGetValue(name, out var list) ? list : Array.Empty<string>();
}
