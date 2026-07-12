using System.Diagnostics;
using BitBroom.Core.Engine;

namespace BitBroom.Core.Special;

/// <summary>
/// Removes C:\Windows.old (previous Windows installation). Windows deletes it automatically
/// ~10 days after an upgrade; removing it earlier frees 10–30+ GB but permanently disables
/// the "Go back" rollback. The folder is TrustedInstaller-owned, so removal takes ownership
/// first (takeown + icacls), exactly like Microsoft's own cleanup does under the hood.
/// Advanced risk, off by default, explicit warning.
/// </summary>
public sealed class WindowsOldCategory : CleanCategory
{
    public const string CategoryId = "windows-old";

    public static WindowsOldCategory Create() => new()
    {
        Id = CategoryId,
        Name = "Previous Windows installation (Windows.old)",
        Description = "The complete previous Windows installation kept for rollback after an upgrade. Deleting it frees a lot of space but permanently removes the ability to go back to the previous version.",
        Group = CategoryGroup.Advanced,
        Risk = RiskLevel.Advanced,
        EnabledByDefault = false,
        RequiresAdmin = true,
        Warning = "Deleting Windows.old cannot be undone and disables rollback to your previous Windows version. " +
                  "It may also contain files from the old user profiles — check Windows.old\\Users for anything personal first.",
    };

    private static string WindowsOldPath
    {
        get
        {
            string? drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return Path.Combine(drive ?? @"C:\", "Windows.old");
        }
    }

    public override Task<CategoryScanResult> ScanAsync(ScanContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new CategoryScanResult { CategoryId = Id };
            var stopwatch = Stopwatch.StartNew();
            string dir = WindowsOldPath;

            if (!Directory.Exists(dir))
            {
                result.NotDetected = true;
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            long total = 0;
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
                    total += file.Length;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Partial measurement only: {ex.Message}");
            }

            result.TotalBytes = total;
            result.Items.Add(new ScanItem(dir, total, DateTime.UtcNow, dir, ScanItemFlags.Virtual | ScanItemFlags.Directory));
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
        string dir = WindowsOldPath;

        if (!Directory.Exists(dir))
        {
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        if (context.Simulate)
        {
            context.Logger.Deleted(dir, scan.TotalBytes, simulated: true);
            result.Deleted = 1;
            result.BytesFreed = scan.TotalBytes;
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        // Hard safety re-check: the path must be exactly <systemdrive>\Windows.old.
        string? drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (drive is null || !PathGuard.PathsEqual(dir, Path.Combine(drive, "Windows.old")))
        {
            result.Errors.Add("Refused: unexpected Windows.old path.");
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        context.Logger.Info("Taking ownership of Windows.old (takeown/icacls)…");
        progress?.Report(new CleanProgress(Id, Name, 0, 3, 0));

        ProcessResult takeown = await ProcessRunner.RunAsync(
            "takeown.exe", $"/F \"{dir}\" /R /A /D Y", TimeSpan.FromMinutes(20), null, cancellationToken).ConfigureAwait(false);
        if (!takeown.Success)
        {
            context.Logger.Warn($"takeown returned {takeown.ExitCode}; attempting deletion anyway.");
        }

        progress?.Report(new CleanProgress(Id, Name, 1, 3, 0));
        ProcessResult icacls = await ProcessRunner.RunAsync(
            "icacls.exe", $"\"{dir}\" /grant *S-1-5-32-544:F /T /C /Q", TimeSpan.FromMinutes(20), null, cancellationToken).ConfigureAwait(false);
        if (!icacls.Success)
        {
            context.Logger.Warn($"icacls returned {icacls.ExitCode}; attempting deletion anyway.");
        }

        progress?.Report(new CleanProgress(Id, Name, 2, 3, 0));
        context.Logger.Info("Deleting Windows.old…");

        // cmd rd handles long paths and read-only flags better than Directory.Delete here.
        ProcessResult rd = await ProcessRunner.RunAsync(
            "cmd.exe", $"/c rd /s /q \"{dir}\"", TimeSpan.FromMinutes(30), null, cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(dir))
        {
            result.Errors.Add($"Windows.old could not be fully removed (rd exit {rd.ExitCode}). Some files may be in use; try again after a reboot.");
            long remaining = MeasureRemaining(dir);
            result.BytesFreed = Math.Max(0, scan.TotalBytes - remaining);
            result.Locked = 1;
        }
        else
        {
            result.Deleted = 1;
            result.BytesFreed = scan.TotalBytes;
            context.Logger.Deleted(dir, scan.TotalBytes, simulated: false);
        }

        progress?.Report(new CleanProgress(Id, Name, 3, 3, result.BytesFreed));
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private static long MeasureRemaining(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
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
