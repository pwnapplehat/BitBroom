namespace BitBroom.Core.Engine;

/// <summary>
/// Guard for *manual* recycle actions the user triggers in the Analyzer and Duplicates
/// tabs (as opposed to rule-based cleaning, which the stricter <see cref="PathGuard"/>
/// covers). It refuses the places a careless click must never reach — drive roots, the
/// Windows/Program Files/ProgramData trees, the Users root and the user's own profile
/// root, plus the system-managed files at the drive root (pagefile, hiberfil, the bin,
/// System Volume Information). User content deeper inside the profile or on data drives
/// stays deletable (it is confirmed and goes to the Recycle Bin), so the tools remain
/// useful without being dangerous.
/// </summary>
public static class ManualDeleteGuard
{
    private static readonly string[] ProtectedRoots = BuildProtectedRoots();

    /// <summary>Folder/file names that are off-limits when they sit directly at a drive root.</summary>
    private static readonly string[] ProtectedDriveRootNames =
    [
        "$Recycle.Bin", "$RECYCLE.BIN", "System Volume Information", "Recovery",
        "$WinREAgent", "Config.Msi", "$SysReset", "$GetCurrent", "$Windows.~BT", "$Windows.~WS",
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
    ];

    private static string[] BuildProtectedRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                roots.Add(PathGuard.Normalize(path));
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // The Users root (parent of every profile).
        string? windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? drive = Path.GetPathRoot(windows);
        if (!string.IsNullOrEmpty(drive))
        {
            Add(Path.Combine(drive, "Users"));
        }

        return [.. roots];
    }

    /// <summary>Null when the path may be recycled manually, else a human-readable reason.</summary>
    public static string? Validate(string path)
    {
        // Reject relative/empty input on the RAW string — never resolve it against the
        // current directory (which would silently turn "foo" into a real absolute path).
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return "it is not a full, absolute path";
        }

        string normalized;
        try
        {
            normalized = PathGuard.Normalize(path);
        }
        catch (Exception)
        {
            return "the path is malformed";
        }

        if (PathGuard.IsDriveRoot(normalized))
        {
            return "it is a drive root";
        }

        // Content strictly inside the user's own profile (Downloads, Documents, AppData…)
        // is the user's to recycle — allowed even though the profile sits under the Users
        // root. The profile root itself is NOT allowed (it falls through to the loop).
        string? profile = SafeNormalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile is not null && PathGuard.IsUnder(normalized, profile))
        {
            return null;
        }

        foreach (string root in ProtectedRoots)
        {
            if (PathGuard.PathsEqual(normalized, root))
            {
                return "it is a protected system or profile folder";
            }

            if (PathGuard.IsUnder(normalized, root))
            {
                return "it is inside a protected system folder";
            }
        }

        // A protected name sitting directly at a drive root (e.g. C:\pagefile.sys, C:\$Recycle.Bin).
        string? parent = Path.GetDirectoryName(normalized);
        if (parent is not null && PathGuard.IsDriveRoot(parent))
        {
            string leaf = Path.GetFileName(normalized);
            foreach (string name in ProtectedDriveRootNames)
            {
                if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase))
                {
                    return "it is a system-managed file at the drive root";
                }
            }
        }

        return null;
    }

    public static bool CanDelete(string path) => Validate(path) is null;

    private static string? SafeNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return PathGuard.Normalize(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
