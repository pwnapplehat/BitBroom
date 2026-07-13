namespace BitBroom.Core.Engine;

/// <summary>
/// Shared detector for "this path belongs to an INSTALLED application" — not a user
/// document, not a dev project. Installed desktop apps ship the exact same folder layout
/// as a dev project: an Electron app (Cursor, Discord, Slack, VS Code, Obsidian…) is
/// literally <c>package.json</c> + <c>out</c>/<c>dist</c> + <c>node_modules</c>, and
/// Squirrel-packaged apps drop an <c>Update.exe</c> beside versioned app folders. Name- and
/// manifest-based heuristics therefore cannot, on their own, tell an app's own runtime code
/// apart from disposable build junk.
///
/// Every manual or heuristic deletion surface consults this so none of them can recycle an
/// application's runtime files:
///   • the dev-junk finder (auto-identifies build folders),
///   • the duplicate finder (an app's DLL/asset can be byte-identical to another copy),
///   • the empty-folder finder (apps expect certain empty dirs to exist),
///   • the Analyzer's manual recycle.
///
/// Detection is by physical evidence, not a name list, so it adapts to any install location
/// (Program Files is already covered elsewhere; this catches per-user installs under
/// %LOCALAPPDATA%\Programs, Squirrel apps under %LOCALAPPDATA%, and portable apps anywhere
/// on any drive). Fails closed: an unreadable ancestor is treated as "inside an app".
/// </summary>
public static class RuntimeAppGuard
{
    /// <summary>
    /// Files whose presence marks a directory as an installed/deployed application:
    /// Electron/Chromium runtime data and the Squirrel.Windows updater stub.
    /// </summary>
    public static readonly string[] MarkerFiles =
    [
        "icudtl.dat",                // Electron/Chromium ICU data — sits beside the app exe
        "v8_context_snapshot.bin",   // Electron/Chromium V8 snapshot
        "chrome_100_percent.pak",    // Chromium resource pak
        "Update.exe",                // Squirrel.Windows updater stub (Discord, Slack, Teams…)
    ];

    private static readonly string? ProgramsRoot = BuildProgramsRoot();

    private static string? BuildProgramsRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(local, "Programs")));
    }

    /// <summary>
    /// %LOCALAPPDATA%\Programs\* — the per-user install root used by Electron apps and
    /// "just for me" installers (Cursor, VS Code user setup, many others).
    /// </summary>
    public static bool IsPerUserProgramInstall(string fullPath)
    {
        if (ProgramsRoot is null)
        {
            return false;
        }

        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            return string.Equals(full, ProgramsRoot, StringComparison.OrdinalIgnoreCase)
                || IsUnder(full, ProgramsRoot);
        }
        catch (Exception)
        {
            return true; // unparseable → fail closed
        }
    }

    /// <summary>True when the directory itself carries app-runtime marker files or an *.asar.</summary>
    public static bool DirectoryHasMarkers(string dir)
    {
        try
        {
            foreach (string marker in MarkerFiles)
            {
                if (File.Exists(Path.Combine(dir, marker)))
                {
                    return true;
                }
            }

            return new DirectoryInfo(dir).EnumerateFiles("*.asar").Any();
        }
        catch (Exception)
        {
            return true; // unreadable → fail closed
        }
    }

    /// <summary>
    /// True when the path sits in a tree whose directory — or any ancestor — carries
    /// app-runtime markers. This is how a packed/portable Electron app anywhere (Desktop,
    /// D:\Apps, a USB stick) reveals itself even without a fixed install prefix.
    /// </summary>
    public static bool IsInAppRuntimeTree(string fullPath)
    {
        try
        {
            var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath)));
            for (int depth = 0; dir is not null && depth < 64; depth++, dir = dir.Parent)
            {
                if (!dir.Exists)
                {
                    continue; // a not-yet-existing leaf is not evidence either way
                }

                if (DirectoryHasMarkers(dir.FullName))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return true; // fail closed
        }
    }

    /// <summary>
    /// True when the path belongs to an installed application: a per-user program install,
    /// or any tree carrying app-runtime markers. Program Files / ProgramData / Windows are
    /// covered by <see cref="ManualDeleteGuard"/>'s protected roots and aren't repeated here.
    /// </summary>
    public static bool IsInstalledApp(string fullPath)
        => IsPerUserProgramInstall(fullPath) || IsInAppRuntimeTree(fullPath);

    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length &&
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        path[root.Length] == Path.DirectorySeparatorChar;
}
