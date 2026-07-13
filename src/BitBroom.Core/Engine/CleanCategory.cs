using System.Diagnostics;
using BitBroom.Core.Logging;

namespace BitBroom.Core.Engine;

/// <summary>Shared context for scan operations.</summary>
public sealed class ScanContext
{
    public required PathResolver Resolver { get; init; }
    public required int GlobalMinAgeHours { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    public RunLogger? Logger { get; init; }
    public ExclusionSet Exclusions { get; init; } = ExclusionSet.Empty;
}

/// <summary>Shared context for clean operations.</summary>
public sealed class CleanContext
{
    public required PathResolver Resolver { get; init; }
    public required RunLogger Logger { get; init; }
    public required bool Simulate { get; init; }
    public ExclusionSet Exclusions { get; init; } = ExclusionSet.Empty;

    /// <summary>Send deleted files to the Recycle Bin instead of removing them permanently.</summary>
    public bool UseRecycleBin { get; init; }
}

/// <summary>
/// A cleaning category. Rule-based categories are fully declarative; special categories
/// (Recycle Bin, Event Logs, Windows.old, Delivery Optimization) override Scan/Clean.
/// </summary>
public class CleanCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required CategoryGroup Group { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;

    /// <summary>Selected by default in the UI/CLI "defaults" set. Only ever true for Safe categories.</summary>
    public bool EnabledByDefault { get; init; }

    /// <summary>Requires an elevated process to be useful.</summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>Extra caution note surfaced in the UI before cleaning.</summary>
    public string? Warning { get; init; }

    public IReadOnlyList<CleanRule> Rules { get; init; } = [];

    public virtual Task<CategoryScanResult> ScanAsync(ScanContext context, CancellationToken cancellationToken)
        => Task.Run(() => ScanRules(context, cancellationToken), cancellationToken);

    protected CategoryScanResult ScanRules(ScanContext context, CancellationToken cancellationToken)
    {
        var result = new CategoryScanResult { CategoryId = Id };
        var stopwatch = Stopwatch.StartNew();
        bool anyRootExisted = false;

        foreach (CleanRule rule in Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<ResolvedRoot> roots;
            try
            {
                roots = context.Resolver.ExpandRoots(rule,
                    (root, reason) => result.Errors.Add($"Rejected root {root}: {reason}"));
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Rule '{rule.RelativePattern}': {ex.Message}");
                continue;
            }

            foreach (ResolvedRoot root in roots)
            {
                anyRootExisted = true;
                var stats = new FileSystemWalker.WalkStats();
                try
                {
                    IEnumerable<ScanItem> items = rule.Kind == RuleKind.FixedFiles
                        ? FileSystemWalker.ResolveFixedFiles(root, context.NowUtc, context.GlobalMinAgeHours, stats, context.Exclusions)
                        : FileSystemWalker.Walk(root, context.NowUtc, context.GlobalMinAgeHours, stats, cancellationToken, context.Exclusions);

                    foreach (ScanItem item in items)
                    {
                        result.Items.Add(item);
                        result.TotalBytes += item.SizeBytes;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Walk failed under {root.Path}: {ex.Message}");
                }

                result.SkippedReparsePoints += stats.SkippedReparsePoints;
                result.SkippedCloudPlaceholders += stats.SkippedCloudPlaceholders;
                result.SkippedTooNew += stats.SkippedTooNew;
                result.Inaccessible += stats.Inaccessible;
                result.SkippedExcluded += stats.SkippedExcluded;
            }
        }

        result.NotDetected = !anyRootExisted;
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    public virtual Task<CategoryCleanResult> CleanAsync(
        CleanContext context,
        CategoryScanResult scan,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
        => Task.Run(() => CleanScannedItems(context, scan, progress, cancellationToken), cancellationToken);

    protected CategoryCleanResult CleanScannedItems(
        CleanContext context,
        CategoryScanResult scan,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new CategoryCleanResult { CategoryId = Id, Simulated = context.Simulate };
        var stopwatch = Stopwatch.StartNew();
        DeleteMode mode = context.Simulate
            ? DeleteMode.Simulate
            : context.UseRecycleBin ? DeleteMode.RecycleBin : DeleteMode.Permanent;
        var deleter = new SafeDeleter(context.Resolver.Guard, context.Logger, mode, context.Exclusions);

        // Track directories per root for the empty-directory pass.
        var directoriesByRoot = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        int done = 0;
        foreach (ScanItem item in scan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DeleteOutcome outcome = deleter.DeleteFile(item);
            switch (outcome)
            {
                case DeleteOutcome.Deleted:
                case DeleteOutcome.Simulated:
                    result.Deleted++;
                    result.BytesFreed += item.SizeBytes;
                    break;
                case DeleteOutcome.Locked:
                    result.Locked++;
                    break;
                case DeleteOutcome.AccessDenied:
                    result.AccessDenied++;
                    break;
                case DeleteOutcome.Missing:
                    result.Missing++;
                    break;
                case DeleteOutcome.GuardRejected:
                    result.SkippedByGuard++;
                    break;
                default:
                    break;
            }

            string? parent = Path.GetDirectoryName(item.Path);
            if (parent is not null && !PathGuard.PathsEqual(parent, item.RootPath))
            {
                if (!directoriesByRoot.TryGetValue(item.RootPath, out HashSet<string>? set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    directoriesByRoot[item.RootPath] = set;
                }

                // Register the parent chain up to (excluding) the root.
                string? cursor = parent;
                while (cursor is not null && PathGuard.IsUnder(cursor, item.RootPath))
                {
                    set.Add(cursor);
                    cursor = Path.GetDirectoryName(cursor);
                }
            }

            done++;
            if (done % 128 == 0 || done == scan.Items.Count)
            {
                progress?.Report(new CleanProgress(Id, Name, done, scan.Items.Count, result.BytesFreed));
            }
        }

        foreach ((string root, HashSet<string> dirs) in directoriesByRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool deleteEmpty = Rules.Count == 0 || Rules.Any(r => r.DeleteEmptyDirs);
            if (deleteEmpty)
            {
                result.EmptyDirsRemoved += deleter.RemoveEmptyDirectories([.. dirs], root);
            }
        }

        result.Duration = stopwatch.Elapsed;
        return result;
    }
}
