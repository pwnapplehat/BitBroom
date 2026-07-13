using BitBroom.Core.Special;
using BitBroom.Core.Util;
using Microsoft.Win32;

namespace BitBroom.Core.Hogs;

public enum HogSeverity
{
    Info,
    Notable,
    Critical,
}

public sealed record HogItem(
    string Id,
    string Title,
    long? SizeBytes,
    string Detail,
    string Guidance,
    HogSeverity Severity,
    string? Path = null);

/// <summary>
/// Report-only detector for the big, hidden disk consumers that classic cleaners miss —
/// the things behind most "my C: drive filled up for no reason" threads:
/// hibernation/page files, WSL & Docker virtual disks, System Restore, the search index,
/// the component store, driver store, Windows Installer folder, browser profiles, OSTs,
/// and the 2026 CapabilityAccessManager.db-wal Windows bug. Nothing here deletes anything;
/// every item explains the safe, supported remediation instead.
/// </summary>
public sealed class SpaceHogInspector
{
    public async Task<List<HogItem>> InspectAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task<HogItem?>>
        {
            Task.Run(InspectHibernationFile, cancellationToken),
            Task.Run(InspectPageFiles, cancellationToken),
            Task.Run(InspectCapabilityAccessManager, cancellationToken),
            Task.Run(InspectSearchIndex, cancellationToken),
            Task.Run(InspectComponentStore, cancellationToken),
            Task.Run(InspectDriverStore, cancellationToken),
            Task.Run(InspectInstallerFolder, cancellationToken),
            Task.Run(InspectEventLogStore, cancellationToken),
            Task.Run(InspectOutlookDataFiles, cancellationToken),
            Task.Run(InspectDownloadsFolder, cancellationToken),
            InspectRestorePointsAsync(cancellationToken),
        };

        tasks.AddRange(InspectWslDisks(cancellationToken));

        HogItem?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.Where(r => r is not null).Cast<HogItem>().OrderByDescending(r => r.SizeBytes ?? 0)];
    }

    // -------------------------------------------------------------------------

    private static string SystemDrive =>
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    private static long? FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long DirectorySize(string path, CancellationToken cancellationToken = default)
    {
        long total = 0;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                RecurseSubdirectories = true,
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (Exception)
        {
            // Partial totals are fine for reporting.
        }

        return total;
    }

    private HogItem? InspectHibernationFile()
    {
        long? size = FileSize(Path.Combine(SystemDrive, "hiberfil.sys"));
        if (size is null or 0)
        {
            return null;
        }

        return new HogItem(
            "hiberfil",
            "Hibernation file (hiberfil.sys)",
            size,
            "Stores RAM contents for Hibernate and Fast Startup. Sized at roughly 40% of installed RAM by default.",
            "If you never use Hibernate: Tools → “Disable hibernation” (powercfg /h off) removes the file entirely. " +
            "To keep Fast Startup with a smaller file: powercfg /h /type reduced.",
            HogSeverity.Notable,
            Path.Combine(SystemDrive, "hiberfil.sys"));
    }

    private HogItem? InspectPageFiles()
    {
        long total = 0;
        var paths = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            foreach (string name in new[] { "pagefile.sys", "swapfile.sys" })
            {
                long? size = FileSize(Path.Combine(drive.RootDirectory.FullName, name));
                if (size is > 0)
                {
                    total += size.Value;
                    paths.Add(Path.Combine(drive.RootDirectory.FullName, name));
                }
            }
        }

        if (total == 0)
        {
            return null;
        }

        return new HogItem(
            "pagefile",
            "Page file & swap file",
            total,
            $"Virtual-memory backing files ({string.Join(", ", paths)}). Windows manages their size automatically.",
            "Leave system-managed unless you know better. Shrinking or disabling the page file can cause out-of-memory crashes; if space is critical, move it to another drive via System → Advanced system settings → Performance → Virtual memory.",
            HogSeverity.Info);
    }

    private HogItem? InspectCapabilityAccessManager()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "CapabilityAccessManager");
        long total = 0;
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*.db*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                }))
                {
                    total += file.Length;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        // Normal size is a few MB; only surface it when it's clearly wrong.
        if (total < 512L * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "camsvc-wal",
            "CapabilityAccessManager database (known Windows bug)",
            total,
            "A Windows 11 24H2/25H2 bug lets CapabilityAccessManager.db-wal grow to hundreds of GB (Microsoft fixed it in KB5095093, June 2026).",
            "Install the June 2026 optional update KB5095093 (or the July 2026 Patch Tuesday). If the drive is already full: boot to Safe Mode, stop the 'camsvc' service and delete only the .db-wal file — never the main .db. Turning off Settings → Privacy → Location also stops the runaway growth.",
            HogSeverity.Critical,
            dir);
    }

    private HogItem? InspectSearchIndex()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Search", "Data", "Applications", "Windows");
        long total = 0;
        try
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }

            foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("Windows.*db*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            }))
            {
                total += file.Length;
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (total < 1L * 1024 * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "search-index",
            "Windows Search index (Windows.db / Windows.edb)",
            total,
            "The search index grows with indexed content — Outlook PST/OST indexing is the classic cause of multi-GB indexes.",
            "Rebuild it: Settings → Privacy & security → Searching Windows → Advanced indexing options → Advanced → Rebuild. Exclude folders/Outlook there to keep it small.",
            HogSeverity.Notable,
            dir);
    }

    private HogItem? InspectComponentStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        long size = DirectorySize(dir);
        return new HogItem(
            "winsxs",
            "Windows component store (WinSxS)",
            size,
            "Holds every Windows component plus superseded versions kept for update rollback. Apparent size overstates real usage — many files are hardlinks shared with System32.",
            "Never delete files manually (it bricks servicing). Use Tools → “Component store cleanup” (DISM /StartComponentCleanup) — Microsoft's supported method. BitBroom deliberately does not run /ResetBase for you: it permanently removes update-uninstall ability.",
            HogSeverity.Info,
            dir);
    }

    private HogItem? InspectDriverStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        long size = DirectorySize(dir);
        if (size < 2L * 1024 * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "driverstore",
            "Driver store (FileRepository)",
            size,
            "Staged driver packages. GPU vendors leave 1 GB+ per driver update behind; Windows never prunes old versions.",
            "BitBroom can clean this: Tools → 'Remove old drivers' deletes superseded versions via pnputil, keeping the newest of every driver family (admin required). Never delete files from the folder directly.",
            HogSeverity.Notable,
            dir);
    }

    private HogItem? InspectInstallerFolder()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        long size = DirectorySize(dir);
        if (size < 2L * 1024 * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "msi-installer",
            @"Windows Installer cache (C:\Windows\Installer)",
            size,
            "Cached MSI/MSP packages required to repair, update and uninstall installed software.",
            "Do NOT delete — apps become un-uninstallable (BitBroom refuses to touch it by design). Space here shrinks by uninstalling unused software.",
            HogSeverity.Info,
            dir);
    }

    private HogItem? InspectEventLogStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "winevt", "Logs");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        long size = DirectorySize(dir);
        if (size < 1L * 1024 * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "event-log-store",
            "Windows event log store",
            size,
            "Accumulated .evtx event logs.",
            "The 'Windows Event Logs' category (Advanced) clears them if you don't need the diagnostic history.",
            HogSeverity.Info,
            dir);
    }

    private HogItem? InspectOutlookDataFiles()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Outlook");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        long total = 0;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*.ost", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            }))
            {
                total += file.Length;
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (total < 5L * 1024 * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "outlook-ost",
            "Outlook offline mailboxes (.ost)",
            total,
            "Locally cached copies of your mailboxes.",
            "Never delete OST files by hand while Outlook is configured. Shrink instead: Outlook → Account Settings → change 'Mail to keep offline' to 3–12 months; the OST compacts over time.",
            HogSeverity.Notable,
            dir);
    }

    private HogItem? InspectDownloadsFolder()
    {
        string? downloads = Native.NativeMethods.GetDownloadsFolderPath();
        if (downloads is null || !Directory.Exists(downloads))
        {
            return null;
        }

        long size = DirectorySize(downloads);
        if (size < 5L * 1024 * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "downloads",
            "Downloads folder",
            size,
            "Your Downloads folder — old installers, ISOs and archives pile up here. BitBroom never auto-cleans it: it is user content.",
            "Review it yourself in the Analyzer tab (sorted by size), or let Storage Sense age files out: Settings → System → Storage → Storage Sense.",
            HogSeverity.Notable,
            downloads);
    }

    private IEnumerable<Task<HogItem?>> InspectWslDisks(CancellationToken cancellationToken)
    {
        yield return Task.Run<HogItem?>(() =>
        {
            var disks = new List<(string Path, long Size)>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            void Probe(string root, string pattern, bool recurse)
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
                        RecurseSubdirectories = recurse,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MaxRecursionDepth = 4,
                    }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long? size = FileSize(file);
                        if (size is > 0)
                        {
                            disks.Add((file, size.Value));
                        }
                    }
                }
                catch (Exception)
                {
                    // Probe failures are fine.
                }
            }

            // Distro disks (Store distros), shared WSL data, Docker Desktop's data disk.
            Probe(Path.Combine(localAppData, "Packages"), "ext4.vhdx", recurse: true);
            Probe(Path.Combine(localAppData, "wsl"), "*.vhdx", recurse: true);
            Probe(Path.Combine(localAppData, "Docker", "wsl"), "*.vhdx", recurse: true);

            if (disks.Count == 0)
            {
                return null;
            }

            long total = disks.Sum(d => d.Size);
            string detail = string.Join("\n", disks.OrderByDescending(d => d.Size)
                .Select(d => $"{ByteFormatter.Format(d.Size),10}  {d.Path}"));

            return new HogItem(
                "wsl-vhdx",
                "WSL / Docker virtual disks (.vhdx)",
                total,
                $"Dynamically-growing virtual disks that never shrink on their own:\n{detail}",
                "BitBroom can compact these: Tools → 'Compact WSL / Docker disks' runs the full recipe " +
                "(fstrim inside each distro, wsl --shutdown, diskpart compact — admin required, quit Docker Desktop first). " +
                "For the biggest wins clean inside first ('docker system prune -a' / delete files in the distro). " +
                "Newer WSL also supports 'wsl --manage <distro> --set-sparse true' for automatic shrinking.",
                total > 20L * 1024 * 1024 * 1024 ? HogSeverity.Critical : HogSeverity.Notable);
        }, cancellationToken);
    }

    private async Task<HogItem?> InspectRestorePointsAsync(CancellationToken cancellationToken)
    {
        if (!ElevationInfo.IsElevated)
        {
            return new HogItem(
                "restore-points",
                "System Restore / shadow copies",
                null,
                "Restore points live in the hidden 'System Volume Information' folder and can quietly consume up to 10%+ of a drive.",
                "Run BitBroom as administrator to see usage, or check with 'vssadmin list shadowstorage' in an elevated terminal. " +
                "Cap usage in System Properties → System Protection → Configure.",
                HogSeverity.Info);
        }

        ProcessResult result = await ProcessRunner.RunAsync("vssadmin.exe", "list shadowstorage",
            TimeSpan.FromSeconds(30), null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        // Parse "Used Shadow Copy Storage space: 12.5 GB (...)" lines (locale: English sizes).
        long totalBytes = 0;
        foreach (string line in result.Output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Used Shadow Copy Storage space:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalBytes += ParseVssSize(trimmed);
        }

        if (totalBytes < 512L * 1024 * 1024)
        {
            return null;
        }

        return new HogItem(
            "restore-points",
            "System Restore / shadow copies",
            totalBytes,
            "Space consumed by restore points and shadow copies (hidden inside System Volume Information).",
            "Cap or clear via System Properties → System Protection → Configure (or 'vssadmin resize shadowstorage /for=C: /on=C: /maxsize=5%'). " +
            "Deleting all restore points removes your rollback safety net — cap rather than disable.",
            HogSeverity.Notable);
    }

    private static long ParseVssSize(string line)
    {
        try
        {
            int colon = line.IndexOf(':');
            string value = line[(colon + 1)..].Trim();
            int space = value.IndexOf(' ');
            if (space < 0)
            {
                return 0;
            }

            string number = value[..space].Trim();
            string unit = value[(space + 1)..].Trim().Split(' ')[0].ToUpperInvariant();

            if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                return 0;
            }

            double multiplier = unit switch
            {
                "B" or "BYTES" => 1,
                "KB" => 1024d,
                "MB" => 1024d * 1024,
                "GB" => 1024d * 1024 * 1024,
                "TB" => 1024d * 1024 * 1024 * 1024,
                _ => 0,
            };

            return (long)(parsed * multiplier);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
