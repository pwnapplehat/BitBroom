using System.Diagnostics;

namespace BitBroom.Core.Analyzer;

/// <summary>A directory node in the analyzed size tree, children sorted by size descending.</summary>
public sealed class AnalyzerNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long SizeBytes { get; internal set; }
    public long FileCount { get; internal set; }
    public List<AnalyzerNode> Children { get; } = [];
    public bool WasInaccessible { get; internal set; }
}

public sealed record LargeFile(string Path, long SizeBytes, DateTime LastWriteUtc);

/// <summary>Aggregate size/count for one file extension.</summary>
public sealed record FileTypeStat(string Extension, long TotalBytes, long FileCount);

public sealed class AnalyzerResult
{
    public required AnalyzerNode Root { get; init; }
    public required List<LargeFile> LargestFiles { get; init; }

    /// <summary>Extensions by total size, descending (top 30; the rest folded into "other").</summary>
    public required List<FileTypeStat> FileTypes { get; init; }

    public long TotalBytes => Root.SizeBytes;
    public long TotalFiles => Root.FileCount;
    public int InaccessibleDirectories { get; init; }
    public int SkippedReparsePoints { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record AnalyzerProgress(long DirectoriesScanned, long FilesScanned, long BytesSoFar, string CurrentPath);

/// <summary>
/// Parallel directory-tree size analyzer — answers "where did my space go?".
/// Never traverses reparse points (junctions/symlinks/mounts), so sizes are physical-ish
/// and OneDrive placeholder trees don't hydrate. Top-N largest files tracked with a bounded heap.
/// </summary>
public sealed class DiskAnalyzer
{
    private const int TopFilesCount = 100;
    private const int TopTypesCount = 30;

    public async Task<AnalyzerResult> AnalyzeAsync(
        string rootPath,
        IProgress<AnalyzerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string fullRoot = Path.GetFullPath(rootPath);

        var rootNode = new AnalyzerNode
        {
            Name = fullRoot,
            FullPath = fullRoot,
        };

        long directoriesScanned = 0;
        long filesScanned = 0;
        long bytesSoFar = 0;
        int inaccessible = 0;
        int reparseSkipped = 0;

        var topFiles = new PriorityQueue<LargeFile, long>(TopFilesCount + 1);
        var topFilesLock = new object();
        var typeStats = new System.Collections.Concurrent.ConcurrentDictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        long lastReport = 0;

        // First level is enumerated inline so we can parallelize across top-level children.
        List<AnalyzerNode> firstLevel = [];
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(fullRoot).EnumerateFileSystemInfos("*", EnumOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo dir)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Interlocked.Increment(ref reparseSkipped);
                        continue;
                    }

                    var child = new AnalyzerNode { Name = dir.Name, FullPath = dir.FullName };
                    rootNode.Children.Add(child);
                    firstLevel.Add(child);
                }
                else if (entry is FileInfo file)
                {
                    long length = SafeLength(file);
                    rootNode.SizeBytes += length;
                    rootNode.FileCount++;
                    filesScanned++;
                    bytesSoFar += length;
                    OfferTopFile(topFiles, topFilesLock, file, length);
                    AccumulateType(typeStats, file.Name, length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            rootNode.WasInaccessible = true;
            inaccessible++;
        }

        await Parallel.ForEachAsync(
            firstLevel,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16),
                CancellationToken = cancellationToken,
            },
            (node, ct) =>
            {
                ScanDirectory(node, ct);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        foreach (AnalyzerNode child in firstLevel)
        {
            rootNode.SizeBytes += child.SizeBytes;
            rootNode.FileCount += child.FileCount;
        }

        SortTreeBySize(rootNode);

        var largest = new List<LargeFile>();
        lock (topFilesLock)
        {
            while (topFiles.Count > 0)
            {
                largest.Add(topFiles.Dequeue());
            }
        }

        largest.Reverse();

        return new AnalyzerResult
        {
            Root = rootNode,
            LargestFiles = largest,
            FileTypes = FoldTypeStats(typeStats),
            InaccessibleDirectories = inaccessible,
            SkippedReparsePoints = reparseSkipped,
            Duration = stopwatch.Elapsed,
        };

        // ---------------------------------------------------------------------

        void ScanDirectory(AnalyzerNode node, CancellationToken ct)
        {
            var stack = new Stack<AnalyzerNode>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                AnalyzerNode current = stack.Pop();
                Interlocked.Increment(ref directoriesScanned);

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(current.FullPath).EnumerateFileSystemInfos("*", EnumOptions);
                }
                catch (Exception)
                {
                    current.WasInaccessible = true;
                    Interlocked.Increment(ref inaccessible);
                    continue;
                }

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
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Interlocked.Increment(ref reparseSkipped);
                            continue;
                        }

                        var child = new AnalyzerNode { Name = entry.Name, FullPath = entry.FullName };
                        current.Children.Add(child);
                        stack.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        long length = SafeLength(file);
                        current.SizeBytes += length;
                        current.FileCount++;
                        Interlocked.Increment(ref filesScanned);
                        long total = Interlocked.Add(ref bytesSoFar, length);
                        OfferTopFile(topFiles, topFilesLock, file, length);
                        AccumulateType(typeStats, file.Name, length);

                        long now = Environment.TickCount64;
                        long last = Interlocked.Read(ref lastReport);
                        if (now - last > 200 && Interlocked.CompareExchange(ref lastReport, now, last) == last)
                        {
                            progress?.Report(new AnalyzerProgress(
                                Interlocked.Read(ref directoriesScanned),
                                Interlocked.Read(ref filesScanned),
                                total,
                                current.FullPath));
                        }
                    }
                }

            }

            AccumulateSizes(node);
        }
    }

    private static readonly EnumerationOptions EnumOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void OfferTopFile(PriorityQueue<LargeFile, long> heap, object gate, FileInfo file, long length)
    {
        if (length < 16L * 1024 * 1024)
        {
            // Files under 16 MB never make a meaningful top-100 on real disks; skip the lock.
            return;
        }

        lock (gate)
        {
            if (heap.Count < TopFilesCount)
            {
                heap.Enqueue(new LargeFile(file.FullName, length, SafeLastWrite(file)), length);
            }
            else if (heap.TryPeek(out _, out long smallest) && length > smallest)
            {
                heap.Dequeue();
                heap.Enqueue(new LargeFile(file.FullName, length, SafeLastWrite(file)), length);
            }
        }
    }

    private static DateTime SafeLastWrite(FileInfo file)
    {
        try
        {
            return file.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static void AccumulateType(
        System.Collections.Concurrent.ConcurrentDictionary<string, (long Bytes, long Count)> stats,
        string fileName,
        long length)
    {
        string extension = Path.GetExtension(fileName);
        string key = extension.Length is > 1 and <= 12 ? extension.ToLowerInvariant() : "(none)";
        stats.AddOrUpdate(key, (length, 1), (_, prev) => (prev.Bytes + length, prev.Count + 1));
    }

    /// <summary>Top extensions by size; everything past the cut folded into "(other)".</summary>
    private static List<FileTypeStat> FoldTypeStats(
        System.Collections.Concurrent.ConcurrentDictionary<string, (long Bytes, long Count)> stats)
    {
        List<FileTypeStat> ordered = [.. stats
            .Select(kv => new FileTypeStat(kv.Key, kv.Value.Bytes, kv.Value.Count))
            .OrderByDescending(s => s.TotalBytes)];

        if (ordered.Count <= TopTypesCount)
        {
            return ordered;
        }

        List<FileTypeStat> top = [.. ordered.Take(TopTypesCount)];
        long otherBytes = 0, otherCount = 0;
        foreach (FileTypeStat stat in ordered.Skip(TopTypesCount))
        {
            otherBytes += stat.TotalBytes;
            otherCount += stat.FileCount;
        }

        top.Add(new FileTypeStat("(other)", otherBytes, otherCount));
        return top;
    }

    /// <summary>Bottom-up size accumulation for a subtree (iterative post-order; deep trees are safe).</summary>
    private static void AccumulateSizes(AnalyzerNode root)
    {
        var stack = new Stack<(AnalyzerNode Node, bool ChildrenDone)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            (AnalyzerNode node, bool childrenDone) = stack.Pop();
            if (childrenDone)
            {
                foreach (AnalyzerNode child in node.Children)
                {
                    node.SizeBytes += child.SizeBytes;
                    node.FileCount += child.FileCount;
                }

                continue;
            }

            stack.Push((node, true));
            foreach (AnalyzerNode child in node.Children)
            {
                stack.Push((child, false));
            }
        }
    }

    private static void SortTreeBySize(AnalyzerNode root)
    {
        var stack = new Stack<AnalyzerNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            AnalyzerNode node = stack.Pop();
            node.Children.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            foreach (AnalyzerNode child in node.Children)
            {
                stack.Push(child);
            }
        }
    }
}
