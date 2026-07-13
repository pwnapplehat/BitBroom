namespace BitBroom.Core.Engine;

/// <summary>
/// Guard for *manual* recycle actions the user triggers in the Analyzer and Duplicates
/// tabs (as opposed to rule-based cleaning, which the stricter <see cref="PathGuard"/>
/// covers). It refuses the places a careless click must never reach — drive roots, the
/// Windows/Program Files/ProgramData trees, the Users root and the user's own profile
/// root, plus system items at the drive root (detected dynamically by the Hidden+System
/// attributes, with a curated name list as a fallback). User content deeper inside the
/// profile or on data drives stays deletable (it is confirmed and goes to the Recycle
/// Bin), so the tools remain useful without being dangerous.
///
/// The protected roots are resolved from the environment (<see cref="Environment.GetFolderPath"/>),
/// so they adapt to any Windows install drive, localized folder names or relocated profile
/// — nothing here assumes "C:\Windows". Only the drive-root fallback names are literal, and
/// those (pagefile.sys, $Recycle.Bin, …) are OS-invariant.
/// </summary>
public static class ManualDeleteGuard
{
    private static readonly string[] ProtectedRoots = BuildProtectedRoots();

    /// <summary>
    /// Fallback names off-limits at a drive root, used only when the Hidden+System
    /// attribute probe can't read the item (missing/locked). These names are invariant
    /// across Windows installs and locales.
    /// </summary>
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

        // Installed applications look exactly like user data / dev projects (an Electron app
        // is package.json + out + node_modules; a Squirrel app is Update.exe + versioned
        // folders). Recycling a file from an app's own install tree breaks the app — and a
        // duplicate/empty-folder/Analyzer click could otherwise reach one under
        // %LOCALAPPDATA%\Programs or any marker-bearing tree. Refuse those before the
        // profile allowance below opens AppData up.
        if (RuntimeAppGuard.IsInstalledApp(normalized))
        {
            return "it is inside an installed application";
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

        // Items sitting directly at a drive root (e.g. C:\pagefile.sys, C:\$Recycle.Bin).
        // Two layers, dynamic first: anything Windows marks Hidden+System at the root is
        // off-limits (pagefile, hiberfil, swapfile, $Recycle.Bin, System Volume Information,
        // Recovery, Config.Msi… — no name list needed, catches future ones too). The curated
        // name list is a fallback for when the attributes can't be read or the item doesn't
        // exist yet (e.g. unit tests, or a path typed by hand).
        string? parent = Path.GetDirectoryName(normalized);
        if (parent is not null && PathGuard.IsDriveRoot(parent))
        {
            if (IsHiddenSystem(normalized))
            {
                return "it is a hidden system item at the drive root";
            }

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

    /// <summary>True when the item exists and carries both Hidden and System attributes.</summary>
    private static bool IsHiddenSystem(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            const FileAttributes mask = FileAttributes.Hidden | FileAttributes.System;
            return (attrs & mask) == mask;
        }
        catch (Exception)
        {
            // Doesn't exist / unreadable — let the name-list fallback decide.
            return false;
        }
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
