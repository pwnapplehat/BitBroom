using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using BitBroom.Core.Engine;
using BitBroom.Core.Native;

namespace BitBroom.Core.Dupes;

/// <summary>One file inside a duplicate group.</summary>
public sealed record DuplicateFile(string Path, long SizeBytes, DateTime LastWriteUtc);

/// <summary>A set of files with identical content (verified by full SHA-256).</summary>
public sealed class DuplicateGroup
{
    public required string ContentHash { get; init; }
    public required long FileSizeBytes { get; init; }
    public required List<DuplicateFile> Files { get; init; }

    /// <summary>Bytes reclaimable by keeping exactly one copy.</summary>
    public long WastedBytes => FileSizeBytes * (Files.Count - 1);
}

public sealed class DuplicateScanResult
{
    public List<DuplicateGroup> Groups { get; } = [];
    public long TotalWastedBytes { get; set; }
    public long FilesConsidered { get; set; }
    public long BytesHashed { get; set; }
    public int SkippedReparsePoints { get; set; }
    public int SkippedCloudPlaceholders { get; set; }
    public int Inaccessible { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed record DuplicateScanProgress(string Phase, long FilesSeen, long FilesHashed, long BytesHashed, int GroupsFound);

/// <summary>
/// Content-verified duplicate file finder. Three-stage pipeline — group by size, then by
/// a 128 KB head hash, then by full SHA-256 — so large unique files are never fully read.
/// Junctions/symlinks are never traversed, cloud placeholders are never hydrated or
/// hashed, and system directories (Windows, Program Files) are excluded by design:
/// "duplicates" there are managed by Windows (hardlinks, WinSxS) and deleting them breaks
/// apps. Deletion of results goes through <see cref="DuplicateDeleter"/> (Recycle Bin
/// only, keep-one enforced).
/// </summary>
public sealed class DuplicateFinder
{
    private const int HeadHashBytes = 128 * 1024;

    private readonly ExclusionSet _exclusions;

    public DuplicateFinder(ExclusionSet? exclusions = null)
    {
        _exclusions = exclusions ?? ExclusionSet.Empty;
    }

    /// <summary>Directories under the scan root that are never entered.</summary>
    private static readonly string[] SystemDirectorySkips = BuildSystemSkips();

    private static string[] BuildSystemSkips()
    {
        var skips = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                skips.Add(PathGuard.Normalize(path));
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        return [.. skips];
    }

    public async Task<DuplicateScanResult> ScanAsync(
        string rootPath,
        long minFileSizeBytes,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DuplicateScanResult();
        var stopwatch = Stopwatch.StartNew();

        string root = PathGuard.Normalize(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: {root}");
        }

        // ---- Stage 1: enumerate and bucket by size (cheap, no I/O beyond metadata) ----
        var bySize = new Dictionary<long, List<DuplicateFile>>();
        long filesSeen = 0;

        await Task.Run(() =>
        {
            foreach (DuplicateFile file in EnumerateCandidates(root, minFileSizeBytes, result, cancellationToken))
            {
                filesSeen++;
                if (!bySize.TryGetValue(file.SizeBytes, out List<DuplicateFile>? bucket))
                {
                    bucket = [];
                    bySize[file.SizeBytes] = bucket;
                }

                bucket.Add(file);

                if (filesSeen % 4096 == 0)
                {
                    progress?.Report(new DuplicateScanProgress("Enumerating", filesSeen, 0, 0, 0));
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        result.FilesConsidered = filesSeen;

        List<List<DuplicateFile>> sizeGroups = [.. bySize.Values.Where(v => v.Count > 1)];
        bySize.Clear();

        // ---- Stage 2: head hash (first 128 KB) to split same-size groups cheaply ----
        long hashed = 0;
        long bytesHashed = 0;
        int unreadableDuringHash = 0;
        var headGroups = new ConcurrentDictionary<(long Size, string HeadHash), ConcurrentBag<DuplicateFile>>();

        await Parallel.ForEachAsync(
            sizeGroups.SelectMany(g => g),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                CancellationToken = cancellationToken,
            },
            async (file, ct) =>
            {
                string? headHash = await HashFileAsync(file.Path, HeadHashBytes, ct).ConfigureAwait(false);
                if (headHash is null)
                {
                    Interlocked.Increment(ref unreadableDuringHash);
                    return;
                }

                long seen = Interlocked.Increment(ref hashed);
                Interlocked.Add(ref bytesHashed, Math.Min(file.SizeBytes, HeadHashBytes));
                headGroups.GetOrAdd((file.SizeBytes, headHash), _ => []).Add(file);

                if (seen % 512 == 0)
                {
                    progress?.Report(new DuplicateScanProgress("Comparing content", filesSeen, seen, Interlocked.Read(ref bytesHashed), 0));
                }
            }).ConfigureAwait(false);

        result.Inaccessible += unreadableDuringHash;

        // ---- Stage 3: full hash for groups still matching (skip when head == whole file) ----
        var finalGroups = new ConcurrentDictionary<(long Size, string FullHash), ConcurrentBag<DuplicateFile>>();

        foreach (((long size, string headHash), ConcurrentBag<DuplicateFile> bag) in headGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bag.Count < 2)
            {
                continue;
            }

            if (size <= HeadHashBytes)
            {
                // The head hash already covered the entire file content.
                finalGroups[(size, headHash)] = bag;
                continue;
            }

            await Parallel.ForEachAsync(
                bag,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
                    CancellationToken = cancellationToken,
                },
                async (file, ct) =>
                {
                    string? fullHash = await HashFileAsync(file.Path, long.MaxValue, ct).ConfigureAwait(false);
                    if (fullHash is null)
                    {
                        return;
                    }

                    Interlocked.Add(ref bytesHashed, file.SizeBytes);
                    finalGroups.GetOrAdd((file.SizeBytes, fullHash), _ => []).Add(file);
                    progress?.Report(new DuplicateScanProgress("Verifying", filesSeen, hashed, Interlocked.Read(ref bytesHashed), 0));
                }).ConfigureAwait(false);
        }

        foreach (((long size, string fullHash), ConcurrentBag<DuplicateFile> bag) in finalGroups)
        {
            if (bag.Count < 2)
            {
                continue;
            }

            var group = new DuplicateGroup
            {
                ContentHash = fullHash,
                FileSizeBytes = size,
                Files = [.. bag.OrderByDescending(f => f.LastWriteUtc)],
            };
            result.Groups.Add(group);
            result.TotalWastedBytes += group.WastedBytes;
        }

        result.Groups.Sort((a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        result.BytesHashed = bytesHashed;
        result.Duration = stopwatch.Elapsed;
        progress?.Report(new DuplicateScanProgress("Done", filesSeen, hashed, bytesHashed, result.Groups.Count));
        return result;
    }

    private IEnumerable<DuplicateFile> EnumerateCandidates(
        string root, long minFileSizeBytes, DuplicateScanResult result, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options);
            }
            catch (Exception)
            {
                result.Inaccessible++;
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
                    result.Inaccessible++;
                    continue;
                }

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (isDirectory)
                    {
                        result.SkippedReparsePoints++;
                    }
                    else
                    {
                        result.SkippedCloudPlaceholders++;
                    }

                    continue;
                }

                if (_exclusions.Count > 0 && _exclusions.IsExcluded(entry.FullName))
                {
                    continue;
                }

                if (isDirectory)
                {
                    string normalized = PathGuard.Normalize(entry.FullName);
                    bool systemSkip = false;
                    foreach (string skip in SystemDirectorySkips)
                    {
                        if (PathGuard.PathsEqual(normalized, skip))
                        {
                            systemSkip = true;
                            break;
                        }
                    }

                    if (!systemSkip)
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
                    result.SkippedCloudPlaceholders++;
                    continue;
                }

                var file = (FileInfo)entry;
                long length;
                DateTime lastWriteUtc;
                try
                {
                    length = file.Length;
                    lastWriteUtc = file.LastWriteTimeUtc;
                }
                catch (Exception)
                {
                    result.Inaccessible++;
                    continue;
                }

                if (length < minFileSizeBytes || length == 0)
                {
                    continue;
                }

                yield return new DuplicateFile(file.FullName, length, lastWriteUtc);
            }
        }
    }

    /// <summary>SHA-256 of up to <paramref name="maxBytes"/> from the file head; null when unreadable.</summary>
    private static async Task<string?> HashFileAsync(string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 16, options: FileOptions.SequentialScan | FileOptions.Asynchronous);

            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long remaining = maxBytes;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                sha.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
