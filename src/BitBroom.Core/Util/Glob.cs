using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BitBroom.Core.Util;

/// <summary>
/// Minimal, predictable wildcard matcher used for file-name and path-segment patterns.
/// Supports '*' (any run of characters) and '?' (single character). Case-insensitive,
/// culture-invariant. No character classes, no '**' semantics — segments are matched
/// individually by the walker, which keeps behaviour easy to reason about and audit.
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string text, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        Regex regex = Cache.GetOrAdd(pattern, Compile);
        return regex.IsMatch(text);
    }

    public static bool IsMatchAny(string text, IReadOnlyList<string> patterns)
    {
        for (int i = 0; i < patterns.Count; i++)
        {
            if (IsMatch(text, patterns[i]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasWildcards(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    private static Regex Compile(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 8);
        sb.Append('^');
        foreach (char c in pattern)
        {
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
