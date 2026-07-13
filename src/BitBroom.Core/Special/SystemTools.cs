using BitBroom.Core.Util;

namespace BitBroom.Core.Special;

/// <summary>
/// One-shot maintenance actions surfaced in the Tools tab. Every action uses the
/// documented, supported mechanism (DISM, powercfg, ipconfig, vssadmin) and streams
/// its console output back to the UI.
/// </summary>
public static class SystemTools
{
    public static Task<ProcessResult> FlushDnsAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(30), onLine, ct);

    public static Task<ProcessResult> AnalyzeComponentStoreAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /AnalyzeComponentStore", TimeSpan.FromMinutes(15), onLine, ct);

    /// <summary>
    /// Microsoft's supported WinSxS cleanup. Deliberately WITHOUT /ResetBase — that switch
    /// permanently removes the ability to uninstall current updates and is not worth the
    /// extra space for most users (see docs/RESEARCH.md).
    /// </summary>
    public static Task<ProcessResult> ComponentStoreCleanupAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", TimeSpan.FromMinutes(60), onLine, ct);

    public static Task<ProcessResult> DisableHibernationAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("powercfg.exe", "/hibernate off", TimeSpan.FromSeconds(30), onLine, ct);

    public static Task<ProcessResult> EnableHibernationAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("powercfg.exe", "/hibernate on", TimeSpan.FromSeconds(30), onLine, ct);

    /// <summary>Keeps Fast Startup but shrinks hiberfil.sys to ~20% of RAM.</summary>
    public static async Task<ProcessResult> ReduceHibernationFileAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        ProcessResult size = await ProcessRunner.RunAsync("powercfg.exe", "/hibernate /size 0", TimeSpan.FromSeconds(30), onLine, ct)
            .ConfigureAwait(false);
        if (!size.Success)
        {
            return size;
        }

        return await ProcessRunner.RunAsync("powercfg.exe", "/hibernate /type reduced", TimeSpan.FromSeconds(30), onLine, ct)
            .ConfigureAwait(false);
    }

    public static Task<ProcessResult> ListShadowStorageAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("vssadmin.exe", "list shadowstorage", TimeSpan.FromSeconds(60), onLine, ct);

    // -------------------------------------------------------------------------
    // WSL / Docker virtual-disk compaction (reclaims empty blocks; non-destructive)
    // -------------------------------------------------------------------------

    /// <summary>Locates every WSL/Docker ext4/vhdx virtual disk under the current profile.</summary>
    public static List<string> FindVirtualDisks()
    {
        var disks = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        void Probe(string root, string pattern)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.None,
                }))
                {
                    if (!disks.Contains(file, StringComparer.OrdinalIgnoreCase))
                    {
                        disks.Add(file);
                    }
                }
            }
            catch (Exception)
            {
                // Probe failures are fine.
            }
        }

        Probe(Path.Combine(localAppData, "Packages"), "ext4.vhdx");
        Probe(Path.Combine(localAppData, "wsl"), "*.vhdx");
        Probe(Path.Combine(localAppData, "Docker", "wsl"), "*.vhdx");
        return disks;
    }

    /// <summary>WSL distro names from the registry (avoids wsl.exe's UTF-16 output entirely).</summary>
    public static List<string> ListWslDistros()
    {
        var names = new List<string>();
        try
        {
            using Microsoft.Win32.RegistryKey? lxss = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null)
            {
                return names;
            }

            foreach (string subKeyName in lxss.GetSubKeyNames())
            {
                using Microsoft.Win32.RegistryKey? sub = lxss.OpenSubKey(subKeyName);
                if (sub?.GetValue("DistributionName") is string name && !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // No WSL or no access — fine.
        }

        return names;
    }

    /// <summary>
    /// Compacts WSL/Docker virtual disks to reclaim empty space. This only shrinks the file
    /// by removing already-free blocks — it never changes the Linux filesystem contents, so
    /// no container, image, or file is lost. Sequence per the documented recipe: best-effort
    /// 'fstrim' inside each distro (tells ext4 to release deleted blocks back to the disk),
    /// then 'wsl --shutdown' (releases the file locks; the user is told to quit Docker
    /// Desktop beforehand), then diskpart's 'compact vdisk', which is built into every
    /// Windows edition (no Hyper-V needed).
    /// </summary>
    public static async Task<ProcessResult> CompactVirtualDisksAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        List<string> disks = FindVirtualDisks();
        if (disks.Count == 0)
        {
            onLine?.Invoke("No WSL or Docker virtual disks found — nothing to compact.");
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        onLine?.Invoke($"Found {disks.Count} virtual disk(s).");

        foreach (string distro in ListWslDistros())
        {
            ct.ThrowIfCancellationRequested();
            onLine?.Invoke($"Trimming free space inside '{distro}' (fstrim)…");
            ProcessResult trim = await ProcessRunner.RunAsync(
                "wsl.exe", $"-d \"{distro}\" -u root fstrim -a", TimeSpan.FromMinutes(5), null, ct)
                .ConfigureAwait(false);
            onLine?.Invoke(trim.Success
                ? $"  '{distro}' trimmed."
                : $"  '{distro}' trim skipped (not supported in this distro — compaction still works, just less thorough).");
        }

        onLine?.Invoke("Shutting down WSL to release file locks…");
        await ProcessRunner.RunAsync("wsl.exe", "--shutdown", TimeSpan.FromMinutes(1), onLine, ct).ConfigureAwait(false);
        await Task.Delay(5000, ct).ConfigureAwait(false); // WSL takes a moment to release the vhdx locks

        long totalBefore = 0, totalAfter = 0;
        int failures = 0;

        foreach (string disk in disks)
        {
            ct.ThrowIfCancellationRequested();

            long before = SafeFileSize(disk);
            totalBefore += before;

            (bool ok, long after) = await CompactSingleVhdxAsync(disk, onLine, ct).ConfigureAwait(false);
            if (!ok)
            {
                failures++;
            }

            totalAfter += after;
        }

        long reclaimed = Math.Max(0, totalBefore - totalAfter);
        onLine?.Invoke($"\nDone. Reclaimed about {ByteFormatter.Format(reclaimed)} across {disks.Count} disk(s).");
        return new ProcessResult(failures == 0 ? 0 : 2, string.Empty, string.Empty);
    }

    /// <summary>
    /// Compacts one dynamically-expanding vhdx via diskpart. The disk is attached
    /// READ-ONLY first because that is what enables diskpart's zero-block scan — without
    /// it 'compact vdisk' only drops blocks already unallocated in the block table and
    /// reclaims nothing (this is also exactly what Optimize-VHD -Mode Full does).
    /// Read-only attach cannot modify the guest filesystem. If anything fails, a
    /// separate detach pass always runs so the disk is never left attached.
    /// </summary>
    public static async Task<(bool Success, long SizeAfter)> CompactSingleVhdxAsync(
        string diskPath, Action<string>? onLine, CancellationToken ct = default)
    {
        long before = SafeFileSize(diskPath);
        onLine?.Invoke($"\nCompacting {diskPath} ({ByteFormatter.Format(before)})…");

        // diskpart reads its commands from a script file (it has no inline-command switch).
        string script = Path.Combine(Path.GetTempPath(), $"bitbroom-compact-{Guid.NewGuid():N}.txt");
        bool success;
        try
        {
            await File.WriteAllTextAsync(script,
                $"select vdisk file=\"{diskPath}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n", ct)
                .ConfigureAwait(false);

            ProcessResult r = await ProcessRunner.RunAsync("diskpart.exe", $"/s \"{script}\"", TimeSpan.FromMinutes(20), onLine, ct)
                .ConfigureAwait(false);
            success = r.Success;

            if (!success)
            {
                // The script aborts at the first failing command, which can leave the
                // read-only attach in place. Always run a best-effort detach pass.
                await File.WriteAllTextAsync(script,
                    $"select vdisk file=\"{diskPath}\"\r\ndetach vdisk\r\nexit\r\n", ct)
                    .ConfigureAwait(false);
                await ProcessRunner.RunAsync("diskpart.exe", $"/s \"{script}\"", TimeSpan.FromMinutes(5), null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (Exception)
            {
                // best-effort
            }
        }

        long after = SafeFileSize(diskPath);
        onLine?.Invoke($"  → now {ByteFormatter.Format(after)} (reclaimed {ByteFormatter.Format(Math.Max(0, before - after))})");
        return (success, after);
    }

    // -------------------------------------------------------------------------
    // OneDrive dehydration (make files online-only; reversible, cloud copy kept)
    // -------------------------------------------------------------------------

    /// <summary>OneDrive folder roots discovered from the standard environment variables.</summary>
    public static List<string> FindOneDriveFolders()
    {
        var roots = new List<string>();
        foreach (string var in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? path = Environment.GetEnvironmentVariable(var);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(path);
            }
        }

        return roots;
    }

    /// <summary>
    /// Makes locally-cached OneDrive files "online-only" to reclaim disk space, the
    /// programmatic equivalent of right-click → "Free up space". This deletes NOTHING: the
    /// cloud copy is authoritative and the file re-downloads on next open. Implemented with
    /// the documented pinned/unpinned file attributes (+U unpins to online-only, -P clears
    /// the always-available pin). Files you explicitly marked "Always keep on this device"
    /// are re-pinned by OneDrive as needed; this simply changes the default state.
    /// </summary>
    public static async Task<ProcessResult> FreeUpOneDriveSpaceAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        List<string> roots = FindOneDriveFolders();
        if (roots.Count == 0)
        {
            onLine?.Invoke("No OneDrive folder detected on this account.");
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        int failures = 0;
        foreach (string root in roots)
        {
            ct.ThrowIfCancellationRequested();
            onLine?.Invoke($"Making files online-only under {root}…");

            // attrib +U -P /s /d : unpin (online-only) + clear always-available, recursively.
            ProcessResult r = await ProcessRunner.RunAsync(
                "attrib.exe", $"+U -P \"{root}\\*\" /s /d", TimeSpan.FromMinutes(10), onLine, ct)
                .ConfigureAwait(false);
            if (!r.Success)
            {
                failures++;
            }
        }

        onLine?.Invoke("\nDone. Files are now online-only; they stay in the cloud and re-download when you open them.");
        return new ProcessResult(failures == 0 ? 0 : 2, string.Empty, string.Empty);
    }

    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // -------------------------------------------------------------------------
    // Superseded driver removal (keep newest of each family; pnputil refuses in-use)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Removes superseded duplicate driver packages from the DriverStore, keeping the newest
    /// version of every driver family. Enumerates via pnputil, prints the plan, then removes
    /// only strictly-older duplicates (pnputil itself refuses any package still bound to a
    /// device). Reports the reclaimed DriverStore size. Requires administrator.
    /// </summary>
    public static async Task<ProcessResult> RemoveOldDriversAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        List<StagedDriver> drivers = await DriverCleanup.EnumerateAsync(onLine, ct).ConfigureAwait(false);
        List<SupersededDriver> superseded = DriverCleanup.FindSuperseded(drivers);

        if (superseded.Count == 0)
        {
            onLine?.Invoke("No superseded driver versions found — nothing to remove.");
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        onLine?.Invoke($"\n{superseded.Count} superseded driver version(s) will be removed (newest of each kept):");
        foreach (SupersededDriver s in superseded)
        {
            onLine?.Invoke($"  remove {s.Driver.PublishedName}  {s.Driver.Provider} {s.Driver.OriginalName} {s.Driver.Version}" +
                           $"   (keeping {s.KeptInstead.Version})");
        }

        string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
        long before = SafeDirectorySize(repo);

        int removed = 0, refused = 0;
        onLine?.Invoke(string.Empty);
        foreach (SupersededDriver s in superseded)
        {
            ct.ThrowIfCancellationRequested();
            (bool ok, string message) = await DriverCleanup.DeleteAsync(s.Driver, null, ct).ConfigureAwait(false);
            onLine?.Invoke(message);
            if (ok)
            {
                removed++;
            }
            else
            {
                refused++;
            }
        }

        long after = SafeDirectorySize(repo);
        onLine?.Invoke($"\nDone. Removed {removed} old driver(s){(refused > 0 ? $", skipped {refused} still in use" : string.Empty)}. " +
                       $"DriverStore: {ByteFormatter.Format(before)} → {ByteFormatter.Format(after)} " +
                       $"(reclaimed {ByteFormatter.Format(Math.Max(0, before - after))}).");
        return new ProcessResult(removed > 0 || refused == 0 ? 0 : 2, string.Empty, string.Empty);
    }

    private static long SafeDirectorySize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            long total = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.None,
            }))
            {
                total += SafeFileSize(file);
            }

            return total;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Restarts Explorer to release thumbnail/icon cache locks.</summary>
    public static async Task<ProcessResult> RestartExplorerAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        onLine?.Invoke("Stopping Explorer…");
        ProcessResult kill = await ProcessRunner.RunAsync("taskkill.exe", "/f /im explorer.exe", TimeSpan.FromSeconds(30), onLine, ct)
            .ConfigureAwait(false);

        await Task.Delay(500, ct).ConfigureAwait(false);
        onLine?.Invoke("Starting Explorer…");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, kill.Output, $"Explorer restart failed: {ex.Message}");
        }

        onLine?.Invoke("Explorer restarted.");
        return new ProcessResult(0, kill.Output, string.Empty);
    }

    /// <summary>
    /// Best-effort restore point creation via PowerShell Checkpoint-Computer.
    /// Windows rate-limits creation (default: one per 24h) — a skip is not an error.
    /// </summary>
    public static async Task<(bool Created, string Message)> TryCreateRestorePointAsync(CancellationToken ct = default)
    {
        if (!ElevationInfo.IsElevated)
        {
            return (false, "Restore point skipped: administrator rights required.");
        }

        ProcessResult result = await ProcessRunner.RunPowerShellAsync(
            "Checkpoint-Computer -Description 'BitBroom before clean' -RestorePointType MODIFY_SETTINGS",
            TimeSpan.FromMinutes(3),
            null,
            ct).ConfigureAwait(false);

        if (result.Success && string.IsNullOrWhiteSpace(result.Error))
        {
            return (true, "Restore point created.");
        }

        string message = result.Error.Trim();
        if (message.Contains("1440", StringComparison.Ordinal) || message.Contains("frequently", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Restore point skipped: Windows already created one in the last 24 hours.");
        }

        if (message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Restore point skipped: System Protection is disabled on this PC.");
        }

        return (false, $"Restore point not created: {(message.Length > 0 ? message : "unknown error")}");
    }
}
