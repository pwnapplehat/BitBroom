using System.Collections.Concurrent;
using BitBroom.Core.Logging;
using BitBroom.Core.Settings;

namespace BitBroom.Core.Engine;

/// <summary>Orchestrates scanning and cleaning across categories.</summary>
public sealed class CleaningEngine
{
    private readonly PathGuard _guard = new();

    public PathGuard Guard => _guard;

    public PathResolver CreateResolver(Dictionary<KnownBase, string?>? baseOverrides = null)
        => new(_guard, baseOverrides);

    /// <summary>Scans the given categories in parallel (bounded).</summary>
    public async Task<Dictionary<string, CategoryScanResult>> ScanAsync(
        IReadOnlyList<CleanCategory> categories,
        AppSettings settings,
        IProgress<ScanProgress>? progress = null,
        Dictionary<KnownBase, string?>? baseOverrides = null,
        CancellationToken cancellationToken = default)
    {
        var resolver = CreateResolver(baseOverrides);
        var context = new ScanContext
        {
            Resolver = resolver,
            GlobalMinAgeHours = settings.MinAgeHours,
            Exclusions = new ExclusionSet(settings.ExcludedPaths),
        };

        var results = new ConcurrentDictionary<string, CategoryScanResult>(StringComparer.OrdinalIgnoreCase);
        long bytesFound = 0;
        int categoriesDone = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(categories, options, async (category, ct) =>
        {
            CategoryScanResult result;
            try
            {
                result = await category.ScanAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new CategoryScanResult { CategoryId = category.Id };
                result.Errors.Add($"Scan crashed: {ex.Message}");
            }

            results[category.Id] = result;
            long total = Interlocked.Add(ref bytesFound, result.TotalBytes);
            int done = Interlocked.Increment(ref categoriesDone);
            progress?.Report(new ScanProgress(category.Id, category.Name, done, categories.Count, total));
        }).ConfigureAwait(false);

        return new Dictionary<string, CategoryScanResult>(results, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Cleans previously scanned categories sequentially (predictable disk churn, clear progress).</summary>
    public async Task<Dictionary<string, CategoryCleanResult>> CleanAsync(
        IReadOnlyList<CleanCategory> categories,
        IReadOnlyDictionary<string, CategoryScanResult> scans,
        AppSettings settings,
        RunLogger logger,
        IProgress<CleanProgress>? progress = null,
        Dictionary<KnownBase, string?>? baseOverrides = null,
        FreeSpaceGoal? freeSpaceGoal = null,
        CancellationToken cancellationToken = default)
    {
        var resolver = CreateResolver(baseOverrides);
        var context = new CleanContext
        {
            Resolver = resolver,
            Logger = logger,
            Simulate = settings.SimulateOnly,
            Exclusions = new ExclusionSet(settings.ExcludedPaths),
            UseRecycleBin = settings.CleanToRecycleBin,
            Goal = freeSpaceGoal,
        };

        var results = new Dictionary<string, CategoryCleanResult>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<CleanCategory> ordered = freeSpaceGoal is null
            ? categories
            : OrderForFreeSpaceGoal(categories, scans, freeSpaceGoal);

        bool loggedGoalStop = false;
        foreach (CleanCategory category in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!scans.TryGetValue(category.Id, out CategoryScanResult? scan))
            {
                continue;
            }

            if (freeSpaceGoal is { IsSatisfied: true })
            {
                if (!loggedGoalStop)
                {
                    logger.Info($"Stopping: {freeSpaceGoal.DriveRoot} has reached the requested free space ({freeSpaceGoal.TargetFreeBytes} bytes).");
                    loggedGoalStop = true;
                }

                results[category.Id] = new CategoryCleanResult { CategoryId = category.Id, Simulated = context.Simulate };
                continue;
            }

            logger.Info($"Cleaning category '{category.Id}' ({scan.Items.Count} items, {scan.TotalBytes} bytes){(context.Simulate ? " [SIMULATION]" : string.Empty)}");

            CategoryCleanResult result;
            try
            {
                result = await category.CleanAsync(context, scan, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new CategoryCleanResult { CategoryId = category.Id, Simulated = context.Simulate };
                result.Errors.Add($"Clean crashed: {ex.Message}");
                logger.Error($"Category '{category.Id}' clean crashed: {ex}");
            }

            results[category.Id] = result;
        }

        return results;
    }

    /// <summary>
    /// Biggest reclaimable pile on the target drive first, so --until-free reaches the
    /// requested free space with fewer (and typically larger) deletions.
    /// </summary>
    private static List<CleanCategory> OrderForFreeSpaceGoal(
        IReadOnlyList<CleanCategory> categories,
        IReadOnlyDictionary<string, CategoryScanResult> scans,
        FreeSpaceGoal goal)
    {
        return [.. categories.OrderByDescending(c => ScoreOnDrive(scans, c.Id, goal))];
    }

    private static long ScoreOnDrive(
        IReadOnlyDictionary<string, CategoryScanResult> scans,
        string categoryId,
        FreeSpaceGoal goal)
    {
        if (!scans.TryGetValue(categoryId, out CategoryScanResult? scan))
        {
            return 0;
        }

        long onDrive = 0;
        bool anyVirtual = false;
        foreach (ScanItem item in scan.Items)
        {
            if ((item.Flags & ScanItemFlags.Virtual) != 0)
            {
                anyVirtual = true;
                continue;
            }

            if (goal.IsOnDrive(item.Path))
            {
                onDrive += item.SizeBytes;
            }
        }

        return anyVirtual ? Math.Max(onDrive, scan.TotalBytes) : onDrive;
    }
}
