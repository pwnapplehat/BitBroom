using System.Diagnostics;
using BitBroom.Core.Engine;

namespace BitBroom.Core.Dupes;

public sealed class EmptyFolderScanResult
{
    /// <summary>Empty folders, including folders that contain only empty folders.</summary>
    public List<string> EmptyFolders { get; } = [];

    public long FoldersScanned { get; set; }
    public int SkippedReparsePoints { get; set; }
    public int Inaccessible { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Finds truly empty directories (no files anywhere below; folders containing only empty
/// folders count as empty). Junctions/symlinks are never traversed and never reported.
/// The scan root itself is never reported. Inaccessible folders are conservatively
/// treated as NON-empty so nothing unknown is ever offered for deletion.
/// </summary>
public sealed class EmptyFolderFinder
{
    private readonly ExclusionSet _exclusions;

    public EmptyFolderFinder(ExclusionSet? exclusions = null)
    {
        _exclusions = exclusions ?? ExclusionSet.Empty;
    }

    public Task<EmptyFolderScanResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var result = new EmptyFolderScanResult();
            var stopwatch = Stopwatch.StartNew();

            string root = PathGuard.Normalize(rootPath);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Scan root does not exist: {root}");
            }

            IsEffectivelyEmpty(root, isRoot: true, result, cancellationToken);
            result.EmptyFolders.Sort(StringComparer.OrdinalIgnoreCase);
            result.Duration = stopwatch.Elapsed;
            return result;
        }, cancellationToken);

    /// <summary>Post-order walk; adds effectively-empty non-root folders to the result.</summary>
    private bool IsEffectivelyEmpty(string dir, bool isRoot, EmptyFolderScanResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        result.FoldersScanned++;

        if (_exclusions.Count > 0 && _exclusions.IsExcluded(dir))
        {
            return false;
        }

        bool empty = true;

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            });

            foreach (FileSystemInfo entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception)
                {
                    result.Inaccessible++;
                    empty = false;
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // A junction/symlink/placeholder counts as content — never look through it,
                    // never delete a folder that holds one.
                    result.SkippedReparsePoints++;
                    empty = false;
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IsEffectivelyEmpty(entry.FullName, isRoot: false, result, ct))
                    {
                        empty = false;
                    }
                }
                else
                {
                    // Any file — even zero bytes — counts as content.
                    empty = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Unreadable folder: assume it has content.
            result.Inaccessible++;
            return false;
        }

        if (empty && !isRoot)
        {
            result.EmptyFolders.Add(dir);
        }

        return empty;
    }
}
