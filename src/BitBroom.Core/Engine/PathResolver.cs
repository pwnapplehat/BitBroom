using BitBroom.Core.Util;

namespace BitBroom.Core.Engine;

/// <summary>A concrete, guard-validated deletion root produced from a rule.</summary>
public sealed record ResolvedRoot(string Path, string BasePath, CleanRule Rule);

/// <summary>
/// Resolves rule bases to absolute paths and expands wildcard segments into concrete
/// directories. Expansion enumerates the real file system (no string substitution) and
/// refuses to descend through reparse points, so a junction placed inside a cache folder
/// can never redirect BitBroom somewhere else.
/// </summary>
public sealed class PathResolver
{
    private readonly PathGuard _guard;
    private readonly Dictionary<KnownBase, string?> _baseOverrides;

    public PathResolver(PathGuard guard, Dictionary<KnownBase, string?>? baseOverrides = null)
    {
        _guard = guard;
        _baseOverrides = baseOverrides ?? [];
    }

    public PathGuard Guard => _guard;

    public string? ResolveBase(KnownBase @base, CleanRule? rule = null)
    {
        if (_baseOverrides.TryGetValue(@base, out string? overridden))
        {
            return overridden;
        }

        return @base switch
        {
            KnownBase.LocalAppData => NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            KnownBase.RoamingAppData => NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            KnownBase.LocalLow => LocalLowPath(),
            KnownBase.UserProfile => NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            KnownBase.ProgramData => NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            KnownBase.SystemRoot => NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            KnownBase.SystemDrive => NullIfEmpty(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))),
            KnownBase.Custom => rule?.CustomBaseProvider?.Invoke(),
            _ => null,
        };
    }

    private static string? LocalLowPath()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Expands a rule into zero or more existing, guard-validated roots.
    /// Rejected roots are reported through <paramref name="onRejected"/> for the audit log.
    /// </summary>
    public List<ResolvedRoot> ExpandRoots(CleanRule rule, Action<string, string>? onRejected = null)
    {
        var results = new List<ResolvedRoot>();
        string? basePath = ResolveBase(rule.Base, rule);
        if (basePath is null || !Directory.Exists(basePath))
        {
            return results;
        }

        basePath = PathGuard.Normalize(basePath);

        if (rule.Kind == RuleKind.FixedFiles)
        {
            // Fixed-file rules resolve later, per file; the base is the root.
            results.Add(new ResolvedRoot(basePath, basePath, rule));
            return results;
        }

        string[] segments = rule.RelativePattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { basePath };

        foreach (string segment in segments)
        {
            var next = new List<string>();
            foreach (string dir in current)
            {
                if (Glob.HasWildcards(segment))
                {
                    IEnumerable<string> matches;
                    try
                    {
                        matches = Directory.EnumerateDirectories(dir, segment, new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.None,
                            MatchCasing = MatchCasing.CaseInsensitive,
                            RecurseSubdirectories = false,
                        });
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (string match in matches)
                    {
                        if (!IsTraversableDirectory(match))
                        {
                            continue;
                        }

                        next.Add(match);
                    }
                }
                else
                {
                    string candidate = Path.Combine(dir, segment);
                    if (Directory.Exists(candidate) && IsTraversableDirectory(candidate))
                    {
                        next.Add(candidate);
                    }
                }
            }

            current = next;
            if (current.Count == 0)
            {
                break;
            }
        }

        foreach (string root in current)
        {
            string normalized = PathGuard.Normalize(root);
            string? rejection = _guard.ValidateRuleRoot(normalized, basePath, trustedCustomBase: rule.Base == KnownBase.Custom);
            if (rejection is null)
            {
                results.Add(new ResolvedRoot(normalized, basePath, rule));
            }
            else
            {
                onRejected?.Invoke(normalized, rejection);
            }
        }

        return results;
    }

    /// <summary>Directory exists and is not a reparse point (junction/symlink/mount).</summary>
    public static bool IsTraversableDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
