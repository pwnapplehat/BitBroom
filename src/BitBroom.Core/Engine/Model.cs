namespace BitBroom.Core.Engine;

public enum RiskLevel
{
    /// <summary>Pure caches and logs; applications regenerate them transparently.</summary>
    Safe = 0,

    /// <summary>Regenerable but with a cost (re-downloads, rebuild time) or minor state loss.</summary>
    Moderate = 1,

    /// <summary>Irreversible consequences (e.g. losing the Windows rollback). Off by default, explicit confirmation.</summary>
    Advanced = 2,
}

public enum CategoryGroup
{
    System = 0,
    Browsers = 1,
    Applications = 2,
    GamingAndGpu = 3,
    Development = 4,
    Advanced = 5,
}

[Flags]
public enum ScanItemFlags
{
    None = 0,

    /// <summary>Not a real file path (e.g. "Recycle Bin"); cleaned via a special operation.</summary>
    Virtual = 1,

    /// <summary>A directory scheduled for removal (only ever produced by special categories such as Windows.old).</summary>
    Directory = 2,
}

/// <summary>A single scanned deletion candidate.</summary>
public readonly record struct ScanItem(string Path, long SizeBytes, DateTime LastWriteUtc, string RootPath, ScanItemFlags Flags = ScanItemFlags.None);

public sealed class CategoryScanResult
{
    public required string CategoryId { get; init; }

    public List<ScanItem> Items { get; } = [];

    public long TotalBytes { get; set; }

    /// <summary>
    /// Overrides the reported item count for special categories whose real count is not
    /// 1:1 with materialized <see cref="Items"/> (e.g. the Recycle Bin reports totals
    /// via the shell API instead of enumerating potentially millions of entries).
    /// </summary>
    public int? VirtualItemCount { get; set; }

    public int FileCount => VirtualItemCount ?? Items.Count;

    /// <summary>Directories skipped because they are junctions/symlinks (never traversed).</summary>
    public int SkippedReparsePoints { get; set; }

    /// <summary>Files skipped because they are cloud placeholders (OneDrive & co.).</summary>
    public int SkippedCloudPlaceholders { get; set; }

    /// <summary>Files skipped because they are younger than the minimum age.</summary>
    public int SkippedTooNew { get; set; }

    /// <summary>Entries skipped because the user excluded their path in Settings.</summary>
    public int SkippedExcluded { get; set; }

    /// <summary>Entries that could not be read (access denied etc.).</summary>
    public int Inaccessible { get; set; }

    /// <summary>True when none of the category's root folders exist (application not installed).</summary>
    public bool NotDetected { get; set; }

    public List<string> Errors { get; } = [];

    public TimeSpan Duration { get; set; }
}

public sealed class CategoryCleanResult
{
    public required string CategoryId { get; init; }

    public long BytesFreed { get; set; }
    public int Deleted { get; set; }
    public int Locked { get; set; }
    public int AccessDenied { get; set; }
    public int Missing { get; set; }
    public int SkippedByGuard { get; set; }
    public int EmptyDirsRemoved { get; set; }
    public bool Simulated { get; set; }

    public List<string> Errors { get; } = [];

    public TimeSpan Duration { get; set; }
}

public sealed record ScanProgress(string CategoryId, string CategoryName, int CategoriesDone, int CategoriesTotal, long BytesFoundSoFar);

public sealed record CleanProgress(string CategoryId, string CategoryName, int ItemsDone, int ItemsTotal, long BytesFreedSoFar);
