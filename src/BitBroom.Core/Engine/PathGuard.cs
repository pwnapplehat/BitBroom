using BitBroom.Core.Native;

namespace BitBroom.Core.Engine;

/// <summary>
/// The single safety authority for every automated deletion BitBroom performs.
///
/// Design rules (see docs/SAFETY.md):
///  1. A rule root must resolve strictly *inside* one of the known safe bases and be at
///     least one path segment deeper than the base itself.
///  2. A rule root may never be — or live inside — a protected location (user content
///     folders, Program Files, System32, WinSxS, profile roots, drive roots, …).
///  3. Rules based on the system drive may only target an explicit allow-list of
///     well-known leftover folders (C:\NVIDIA, C:\AMD, C:\Windows.old, upgrade staging dirs).
///  4. At delete time every path is re-validated to be inside its scanned root, and its
///     attributes are re-read: reparse points (junctions/symlinks) and cloud placeholders
///     are refused outright. BitBroom never traverses or deletes through reparse points —
///     the class of bug behind CCleaner's CVE-2025-3025 junction-following vulnerability.
/// </summary>
public sealed class PathGuard
{
    private readonly List<string> _forbiddenExactRoots = [];
    private readonly List<string> _forbiddenSubtrees = [];
    private readonly List<string> _programFilesSubtrees = [];

    /// <summary>First path segments under the system drive that rules may target.</summary>
    private static readonly string[] SystemDriveAllowList =
    [
        "NVIDIA", "AMD", "Intel",
        "Windows.old",
        "$Windows.~BT", "$Windows.~WS", "$GetCurrent", "$SysReset",
        "ESD",
    ];

    public PathGuard()
    {
        // --- Exact roots that must never themselves be a deletion root -------------
        AddExact(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddExact(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddExact(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddExact(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddExact(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddExact(SystemDrive);
        AddExact(UsersRoot);
        AddExact(Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages"));

        // --- Entire subtrees that are permanently off-limits for rule-based deletion
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddSubtree(NativeMethods.GetDownloadsFolderPath());
        AddSubtree(Environment.GetEnvironmentVariable("OneDrive"));
        AddSubtree(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        AddSubtree(Environment.GetEnvironmentVariable("OneDriveCommercial"));

        // Program Files is off-limits for env-var-based rules, but rules with a trusted
        // custom base (e.g. the Steam install folder read from the registry) may target
        // their own subfolders there — that is where Steam keeps shadercache.
        AddProgramFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddProgramFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            AddSubtree(Combine(windows, "System32"));
            AddSubtree(Combine(windows, "SysWOW64"));
            AddSubtree(Combine(windows, "WinSxS"));
            AddSubtree(Combine(windows, "Fonts"));
            AddSubtree(Combine(windows, "Boot"));
            AddSubtree(Combine(windows, "servicing"));
            AddSubtree(Combine(windows, "SystemApps"));
            AddSubtree(Combine(windows, "SystemResources"));
        }
    }

    private static string SystemDrive
    {
        get
        {
            string? drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return string.IsNullOrEmpty(drive) ? @"C:\" : drive;
        }
    }

    private static string UsersRoot => Combine(SystemDrive, "Users");

    // -------------------------------------------------------------------------
    // Rule-root validation (scan time)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates a fully resolved rule root against a resolved base directory.
    /// Returns null when the root is safe, otherwise a human-readable rejection reason.
    /// <paramref name="trustedCustomBase"/> relaxes only the Program Files subtree check,
    /// for rules whose base comes from a trusted provider (registry-located installs).
    /// </summary>
    public string? ValidateRuleRoot(string rootPath, string basePath, bool trustedCustomBase = false)
    {
        string root = Normalize(rootPath);
        string @base = Normalize(basePath);

        if (!Path.IsPathFullyQualified(root))
        {
            return "root path is not fully qualified";
        }

        if (root.Contains(@"\..") || root.EndsWith("..", StringComparison.Ordinal))
        {
            return "root path contains parent traversal";
        }

        if (IsDriveRoot(root))
        {
            return "root path is a drive root";
        }

        if (!IsUnder(root, @base))
        {
            return $"root escapes its base ({@base})";
        }

        // Depth guard: root must be at least one segment deeper than the base. This is what
        // saves us if an environment variable unexpectedly resolves to something too broad.
        if (Depth(root) < Depth(@base) + 1)
        {
            return "root is not deeper than its base";
        }

        foreach (string exact in _forbiddenExactRoots)
        {
            if (PathsEqual(root, exact))
            {
                return $"root equals protected location ({exact})";
            }
        }

        foreach (string subtree in _forbiddenSubtrees)
        {
            if (PathsEqual(root, subtree) || IsUnder(root, subtree))
            {
                return $"root is inside protected location ({subtree})";
            }
        }

        if (!trustedCustomBase)
        {
            foreach (string subtree in _programFilesSubtrees)
            {
                if (PathsEqual(root, subtree) || IsUnder(root, subtree))
                {
                    return $"root is inside protected location ({subtree})";
                }
            }
        }

        // System-drive rules must target explicitly allow-listed leftovers only.
        if (PathsEqual(@base, Normalize(SystemDrive)))
        {
            string relative = root[Normalize(SystemDrive).TrimEnd('\\').Length..].TrimStart('\\');
            string firstSegment = relative.Split('\\')[0];
            bool allowed = SystemDriveAllowList.Any(a => string.Equals(a, firstSegment, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                return $"system-drive rule targets non-allow-listed folder '{firstSegment}'";
            }
        }

        // Never accept a root that is itself a reparse point.
        try
        {
            var info = new DirectoryInfo(root);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "root is a reparse point (junction/symlink)";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"root attributes unreadable ({ex.GetType().Name})";
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Delete-time validation (defense in depth)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-validates a concrete file path immediately before deletion.
    /// Returns null when the deletion may proceed, otherwise a rejection reason.
    /// </summary>
    public string? ValidateDeletePath(string filePath, string rootPath)
    {
        string file = Normalize(filePath);
        string root = Normalize(rootPath);

        if (!Path.IsPathFullyQualified(file))
        {
            return "path is not fully qualified";
        }

        if (!IsUnder(file, root) && !PathsEqual(file, root))
        {
            return "path escaped its scanned root";
        }

        foreach (string subtree in _forbiddenSubtrees)
        {
            if (PathsEqual(file, subtree) || IsUnder(file, subtree))
            {
                return "path is inside a protected location";
            }
        }

        return null;
    }

    /// <summary>
    /// Attribute-level guard evaluated right before deletion. Refuses reparse points
    /// and cloud placeholders (OneDrive Files On-Demand and similar providers), where
    /// deleting the local placeholder would delete the cloud copy.
    /// </summary>
    public static string? ValidateDeletableAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return "reparse point";
        }

        if ((attributes & FileAttributes.Offline) != 0)
        {
            return "offline/cloud file";
        }

        int raw = (int)attributes;
        if ((raw & NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0 ||
            (raw & NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0)
        {
            return "cloud placeholder";
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------------

    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        // Keep "C:\" for drive roots, trim trailing separators elsewhere.
        if (full.Length > 3)
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full;
    }

    public static bool PathsEqual(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    public static bool IsUnder(string child, string parent)
    {
        string c = Normalize(child);
        string p = Normalize(parent);
        if (c.Length <= p.Length)
        {
            return false;
        }

        string prefix = p.EndsWith('\\') ? p : p + '\\';
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string path)
    {
        string n = Normalize(path);
        return n.Length <= 3 && n.Length >= 2 && n[1] == ':';
    }

    public static int Depth(string path)
    {
        string n = Normalize(path).TrimEnd('\\');
        int depth = 0;
        foreach (char c in n)
        {
            if (c == '\\')
            {
                depth++;
            }
        }

        return depth;
    }

    private static string Combine(string a, string b) => Path.Combine(a, b);

    private void AddExact(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _forbiddenExactRoots.Add(Normalize(path));
        }
    }

    private void AddSubtree(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _forbiddenSubtrees.Add(Normalize(path));
        }
    }

    private void AddProgramFiles(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _programFilesSubtrees.Add(Normalize(path));
        }
    }
}
