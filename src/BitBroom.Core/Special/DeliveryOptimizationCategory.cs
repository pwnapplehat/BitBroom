using System.Diagnostics;
using BitBroom.Core.Engine;

namespace BitBroom.Core.Special;

/// <summary>
/// Clears the Delivery Optimization download cache using the official
/// Delete-DeliveryOptimizationCache cmdlet (with file-measurement for the scan phase).
/// Cache path: %WinDir%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache
/// </summary>
public sealed class DeliveryOptimizationCategory : CleanCategory
{
    public const string CategoryId = "delivery-optimization";

    public static DeliveryOptimizationCategory Create() => new()
    {
        Id = CategoryId,
        Name = "Delivery Optimization cache",
        Description = "Windows Update peer-to-peer download cache. Windows trims it eventually, but it can sit at many gigabytes. Cleared via Microsoft's official cmdlet; content re-downloads if needed.",
        Group = CategoryGroup.System,
        Risk = RiskLevel.Safe,
        EnabledByDefault = true,
        RequiresAdmin = true,
    };

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "ServiceProfiles", "NetworkService", "AppData", "Local",
        "Microsoft", "Windows", "DeliveryOptimization", "Cache");

    public override Task<CategoryScanResult> ScanAsync(ScanContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new CategoryScanResult { CategoryId = Id };
            var stopwatch = Stopwatch.StartNew();
            string dir = CachePath;

            if (!Directory.Exists(dir))
            {
                result.NotDetected = true;
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            try
            {
                foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                    RecurseSubdirectories = true,
                }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Items.Add(new ScanItem(file.FullName, file.Length, file.LastWriteTimeUtc, dir, ScanItemFlags.Virtual));
                    result.TotalBytes += file.Length;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Could not measure Delivery Optimization cache: {ex.Message}");
                result.Inaccessible++;
            }

            result.Duration = stopwatch.Elapsed;
            return result;
        }, cancellationToken);
    }

    public override async Task<CategoryCleanResult> CleanAsync(
        CleanContext context,
        CategoryScanResult scan,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new CategoryCleanResult { CategoryId = Id, Simulated = context.Simulate };
        var stopwatch = Stopwatch.StartNew();

        if (scan.Items.Count == 0)
        {
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        if (context.Simulate)
        {
            context.Logger.Deleted("Delivery Optimization cache", scan.TotalBytes, simulated: true);
            result.Deleted = scan.Items.Count;
            result.BytesFreed = scan.TotalBytes;
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        ProcessResult run = await ProcessRunner.RunPowerShellAsync(
            "Delete-DeliveryOptimizationCache -Force",
            TimeSpan.FromMinutes(5),
            null,
            cancellationToken).ConfigureAwait(false);

        if (run.Success)
        {
            long after = MeasureRemaining();
            result.BytesFreed = Math.Max(0, scan.TotalBytes - after);
            result.Deleted = scan.Items.Count;
            context.Logger.Deleted("Delivery Optimization cache", result.BytesFreed, simulated: false);
        }
        else
        {
            result.Errors.Add($"Delete-DeliveryOptimizationCache failed: {run.Error.Trim()}");
            context.Logger.Error($"Delete-DeliveryOptimizationCache failed: {run.Error.Trim()}");
        }

        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private static long MeasureRemaining()
    {
        try
        {
            if (!Directory.Exists(CachePath))
            {
                return 0;
            }

            return new DirectoryInfo(CachePath)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                    RecurseSubdirectories = true,
                })
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
