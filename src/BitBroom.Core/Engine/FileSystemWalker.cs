using BitBroom.Core.Native;
using BitBroom.Core.Util;

namespace BitBroom.Core.Engine;

/// <summary>
/// Iterative, reparse-point-safe file enumeration used by scanning.
/// Never follows junctions or symlinks; never yields cloud placeholders.
/// </summary>
public static class FileSystemWalker
{
    public sealed class WalkStats
    {
        public int SkippedReparsePoints;
        public int SkippedCloudPlaceholders;
        public int SkippedTooNew;
        public int Inaccessible;

        /// <summary>Entries skipped because the user excluded their path in Settings.</summary>
        public int SkippedExcluded;
    }

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static IEnumerable<ScanItem> Walk(
        ResolvedRoot root,
        DateTime nowUtc,
        int globalMinAgeHours,
        WalkStats stats,
        CancellationToken cancellationToken,
        ExclusionSet? exclusions = null)
    {
        CleanRule rule = root.Rule;
        int minAgeHours = rule.MinAgeHoursOverride ?? globalMinAgeHours;
        DateTime cutoffUtc = nowUtc.AddHours(-minAgeHours);
        exclusions ??= ExclusionSet.Empty;

        if (exclusions.IsExcluded(root.Path))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root.Path);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options);
            }
            catch (Exception)
            {
                stats.Inaccessible++;
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception)
                {
                    stats.Inaccessible++;
                    continue;
                }

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Junction, symlink, mount point, or a per-file reparse (cloud stub):
                    // count and refuse. Directories are NOT traversed.
                    if (isDirectory)
                    {
                        stats.SkippedReparsePoints++;
                    }
                    else
                    {
                        stats.SkippedCloudPlaceholders++;
                    }

                    continue;
                }

                if (exclusions.Count > 0 && exclusions.IsExcluded(entry.FullName))
                {
                    stats.SkippedExcluded++;
                    continue;
                }

                if (isDirectory)
                {
                    if (rule.Recurse)
                    {
                        pending.Push(entry.FullName);
                    }

                    continue;
                }

                int raw = (int)attributes;
                if ((attributes & FileAttributes.Offline) != 0 ||
                    (raw & NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0 ||
                    (raw & NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0)
                {
                    stats.SkippedCloudPlaceholders++;
                    continue;
                }

                if (!Glob.IsMatchAny(entry.Name, rule.FilePatterns))
                {
                    continue;
                }

                var file = (FileInfo)entry;
                DateTime lastWriteUtc;
                DateTime creationUtc;
                long length;
                try
                {
                    lastWriteUtc = file.LastWriteTimeUtc;
                    creationUtc = file.CreationTimeUtc;
                    length = file.Length;
                }
                catch (Exception)
                {
                    stats.Inaccessible++;
                    continue;
                }

                // A file is "new" if either timestamp is fresh: extracted archives get old
                // mtimes but fresh ctimes; copied files get fresh mtimes. Use the newest.
                DateTime effective = lastWriteUtc > creationUtc ? lastWriteUtc : creationUtc;
                if (minAgeHours > 0 && effective > cutoffUtc)
                {
                    stats.SkippedTooNew++;
                    continue;
                }

                yield return new ScanItem(file.FullName, length, lastWriteUtc, root.Path);
            }
        }
    }

    /// <summary>Resolves a fixed-file rule to concrete existing files (same age/attribute rules as Walk).</summary>
    public static IEnumerable<ScanItem> ResolveFixedFiles(
        ResolvedRoot root,
        DateTime nowUtc,
        int globalMinAgeHours,
        WalkStats stats,
        ExclusionSet? exclusions = null)
    {
        CleanRule rule = root.Rule;
        int minAgeHours = rule.MinAgeHoursOverride ?? globalMinAgeHours;
        DateTime cutoffUtc = nowUtc.AddHours(-minAgeHours);
        exclusions ??= ExclusionSet.Empty;

        foreach (string pattern in rule.FilePatterns)
        {
            string candidate = Path.Combine(root.Path, rule.RelativePattern.Length == 0
                ? pattern
                : Path.Combine(rule.RelativePattern, pattern));

            if (exclusions.Count > 0 && exclusions.IsExcluded(candidate))
            {
                stats.SkippedExcluded++;
                continue;
            }

            ScanItem? item = TryDescribeFixedFile(candidate, root.Path, minAgeHours, cutoffUtc, stats);
            if (item.HasValue)
            {
                yield return item.Value;
            }
        }
    }

    private static ScanItem? TryDescribeFixedFile(string candidate, string rootPath, int minAgeHours, DateTime cutoffUtc, WalkStats stats)
    {
        try
        {
            var info = new FileInfo(candidate);
            if (!info.Exists)
            {
                return null;
            }

            string? attributeIssue = PathGuard.ValidateDeletableAttributes(info.Attributes);
            if (attributeIssue is not null)
            {
                stats.SkippedCloudPlaceholders++;
                return null;
            }

            // Same freshness rule as Walk: keep files newer than the cutoff (newest of write/create).
            DateTime effective = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
            if (minAgeHours > 0 && effective > cutoffUtc)
            {
                stats.SkippedTooNew++;
                return null;
            }

            return new ScanItem(info.FullName, info.Length, info.LastWriteTimeUtc, rootPath);
        }
        catch (Exception)
        {
            stats.Inaccessible++;
            return null;
        }
    }
}
