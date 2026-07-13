namespace BitBroom.Core.Engine;

/// <summary>
/// User-configured paths that BitBroom must never scan or delete from. Checked in three
/// layers: root expansion (whole roots skipped), the walker (subtrees pruned during
/// enumeration) and the deleter (final veto right before any deletion).
/// </summary>
public sealed class ExclusionSet
{
    public static readonly ExclusionSet Empty = new([]);

    private readonly List<string> _normalized;

    public ExclusionSet(IEnumerable<string> paths)
    {
        _normalized = [];
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (Path.IsPathFullyQualified(path.Trim()))
                {
                    _normalized.Add(PathGuard.Normalize(path.Trim()));
                }
            }
            catch (Exception)
            {
                // Malformed entries are ignored rather than crashing a scan.
            }
        }
    }

    public int Count => _normalized.Count;

    /// <summary>True when <paramref name="path"/> equals or lives under any excluded path.</summary>
    public bool IsExcluded(string path)
    {
        if (_normalized.Count == 0)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = PathGuard.Normalize(path);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (string excluded in _normalized)
        {
            if (PathGuard.PathsEqual(candidate, excluded) || PathGuard.IsUnder(candidate, excluded))
            {
                return true;
            }
        }

        return false;
    }
}
