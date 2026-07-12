using System.Diagnostics;
using BitBroom.Core.Engine;

namespace BitBroom.Core.Special;

/// <summary>
/// Clears Windows event logs via wevtutil. Off by default and marked Advanced:
/// event logs are valuable for troubleshooting and (in managed environments) auditing.
/// Scan measures the .evtx store under System32\winevt\Logs.
/// </summary>
public sealed class EventLogsCategory : CleanCategory
{
    public const string CategoryId = "event-logs";

    public static EventLogsCategory Create() => new()
    {
        Id = CategoryId,
        Name = "Windows Event Logs",
        Description = "Clears all Windows event logs (Application, System, Setup, and hundreds of service channels). Logs are diagnostic history, not junk — clear them only if you need the space or a fresh slate.",
        Group = CategoryGroup.Advanced,
        Risk = RiskLevel.Advanced,
        EnabledByDefault = false,
        RequiresAdmin = true,
        Warning = "Clearing event logs erases diagnostic history used to investigate crashes, BSODs and security incidents.",
    };

    private static string LogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winevt", "Logs");

    public override Task<CategoryScanResult> ScanAsync(ScanContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new CategoryScanResult { CategoryId = Id };
            var stopwatch = Stopwatch.StartNew();
            string dir = LogsDirectory;

            if (!Directory.Exists(dir))
            {
                result.NotDetected = true;
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            try
            {
                foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*.evtx", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
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
                result.Errors.Add($"Could not measure event logs: {ex.Message}");
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

        if (context.Simulate)
        {
            context.Logger.Deleted($"Event logs ({scan.Items.Count} channels)", scan.TotalBytes, simulated: true);
            result.Deleted = scan.Items.Count;
            result.BytesFreed = scan.TotalBytes;
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        // Enumerate channels via wevtutil (authoritative names, unlike .evtx file names).
        ProcessResult enumerate = await ProcessRunner.RunAsync("wevtutil.exe", "el", TimeSpan.FromMinutes(2), null, cancellationToken)
            .ConfigureAwait(false);
        if (!enumerate.Success)
        {
            result.Errors.Add($"wevtutil el failed: {enumerate.Error.Trim()}");
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        string[] channels = enumerate.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int done = 0;
        foreach (string channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult clear = await ProcessRunner.RunAsync("wevtutil.exe", $"cl \"{channel}\"", TimeSpan.FromSeconds(30), null, cancellationToken)
                .ConfigureAwait(false);
            if (clear.Success)
            {
                result.Deleted++;
            }
            else
            {
                // Many channels legitimately refuse clearing (in use / access denied); count, don't fail.
                result.Locked++;
            }

            done++;
            if (done % 50 == 0 || done == channels.Length)
            {
                progress?.Report(new CleanProgress(Id, Name, done, channels.Length, result.BytesFreed));
            }
        }

        // Space freed = size drop of the .evtx store.
        try
        {
            long after = new DirectoryInfo(LogsDirectory)
                .EnumerateFiles("*.evtx", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None })
                .Sum(f => f.Length);
            result.BytesFreed = Math.Max(0, scan.TotalBytes - after);
        }
        catch (Exception)
        {
            result.BytesFreed = 0;
        }

        context.Logger.Info($"Cleared {result.Deleted} event log channels ({result.Locked} refused), freed {result.BytesFreed} bytes.");
        result.Duration = stopwatch.Elapsed;
        return result;
    }
}
