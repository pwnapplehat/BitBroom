using System.Diagnostics;
using BitBroom.Core.Engine;

namespace BitBroom.Core.Dupes;

/// <summary>A found developer build/dependency directory that is safe to delete and regenerable.</summary>
public sealed record DevArtifact(string Path, string Kind, long SizeBytes, string ProjectPath);

public sealed class DevArtifactScanResult
{
    public List<DevArtifact> Artifacts { get; } = [];
    public long TotalBytes { get; set; }
    public long DirectoriesScanned { get; set; }
    public int SkippedReparsePoints { get; set; }
    public int Inaccessible { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed record DevArtifactProgress(long DirectoriesScanned, int Found, string CurrentPath);

/// <summary>
/// Finds regenerable developer build/dependency directories (node_modules, target, .venv,
/// dist, __pycache__, …). Every match is guarded twice:
///
/// 1. Sibling-manifest check — a folder only qualifies next to a real project manifest
///    (node_modules needs package.json, target needs Cargo.toml/pom.xml, venvs need their
///    own pyvenv.cfg), so a user folder that merely shares the name is never flagged.
///
/// 2. Runtime-app refusal — INSTALLED software ships the same layout as a dev project
///    (Electron apps are literally package.json + out/dist + node_modules; deployed Python
///    tools carry venvs and __pycache__). Deleting those breaks the app, so the finder
///    refuses: anything under AppData (except Temp), Program Files / ProgramData / Windows,
///    dot-folders directly under the user profile (.cursor, .vscode, …), and any tree whose
///    ancestors carry runtime markers (Electron's icudtl.dat / v8_context_snapshot.bin /
///    *.asar, Squirrel's Update.exe). Enforced at scan time AND re-verified at delete time.
///
/// Matched directories are never recursed into. Junctions/symlinks are never followed.
/// Deletion goes through <see cref="DuplicateDeleter"/> (Recycle Bin only).
/// </summary>
public sealed class DevArtifactFinder
{
    private readonly ExclusionSet _exclusions;

    public DevArtifactFinder(ExclusionSet? exclusions = null)
    {
        _exclusions = exclusions ?? ExclusionSet.Empty;
    }

    /// <summary>A recognizable artifact directory and the manifest that proves its project context.</summary>
    private sealed record Rule(string DirName, string Kind, Func<string, bool> ParentQualifies);

    private static bool ParentHas(string parent, params string[] anyOfThese)
    {
        foreach (string name in anyOfThese)
        {
            try
            {
                if (File.Exists(Path.Combine(parent, name)))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Ignore unreadable parents.
            }
        }

        return false;
    }

    private static readonly Rule[] Rules =
    [
        new("node_modules", "Node.js dependencies", p => ParentHas(p, "package.json")),
        new("bower_components", "Bower dependencies", p => ParentHas(p, "bower.json", "package.json")),
        new(".venv", "Python virtualenv", DirHasPyvenv),
        new("venv", "Python virtualenv", DirHasPyvenv),
        new("env", "Python virtualenv", DirHasPyvenv),
        new("__pycache__", "Python bytecode cache", _ => true),
        new("target", "Rust/Maven build output", p => ParentHas(p, "Cargo.toml", "pom.xml")),
        new("dist", "JS build output", p => ParentHas(p, "package.json")),
        new("build", "JS build output", p => ParentHas(p, "package.json")),
        new("out", "JS build output", p => ParentHas(p, "package.json")),
        new(".next", "Next.js build cache", p => ParentHas(p, "package.json")),
        new(".nuxt", "Nuxt build cache", p => ParentHas(p, "package.json")),
        new(".output", "Nuxt/Nitro build output", p => ParentHas(p, "package.json")),
        new(".turbo", "Turborepo cache", p => ParentHas(p, "package.json")),
        new(".parcel-cache", "Parcel cache", p => ParentHas(p, "package.json")),
        new(".gradle", "Gradle project cache", p => ParentHas(p, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts")),
    ];

    /// <summary>Placeholder qualifier for venv rules; the real check is pyvenv.cfg in MatchRule.</summary>
    private static bool DirHasPyvenv(string parentOfVenv) => true;

    public Task<DevArtifactScanResult> ScanAsync(
        string rootPath,
        IProgress<DevArtifactProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var result = new DevArtifactScanResult();
            var stopwatch = Stopwatch.StartNew();

            string root = PathGuard.Normalize(rootPath);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Scan root does not exist: {root}");
            }

            var pending = new Stack<string>();
            pending.Push(root);

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                result.DirectoriesScanned++;

                // A directory carrying app-runtime files (Electron's icudtl.dat, Squirrel's
                // Update.exe, *.asar…) is an installed application — never descend into it.
                if (DirectoryHasRuntimeMarkers(dir))
                {
                    continue;
                }

                IEnumerable<DirectoryInfo> subdirs;
                try
                {
                    subdirs = new DirectoryInfo(dir).EnumerateDirectories("*", options);
                }
                catch (Exception)
                {
                    result.Inaccessible++;
                    continue;
                }

                foreach (DirectoryInfo sub in subdirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileAttributes attrs;
                    try
                    {
                        attrs = sub.Attributes;
                    }
                    catch (Exception)
                    {
                        result.Inaccessible++;
                        continue;
                    }

                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        result.SkippedReparsePoints++;
                        continue;
                    }

                    if (_exclusions.Count > 0 && _exclusions.IsExcluded(sub.FullName))
                    {
                        continue;
                    }

                    // Prune runtime-app territory at the boundary: AppData, Program Files,
                    // ProgramData, Windows, and dot-folders directly under the profile.
                    if (IsRuntimeBoundary(sub.FullName))
                    {
                        continue;
                    }

                    Rule? matched = MatchRule(sub);
                    if (matched is not null)
                    {
                        // Defense in depth: full runtime-location check (prefix roots +
                        // ancestor markers) before the artifact is ever offered.
                        if (IsRuntimeAppLocation(sub.FullName))
                        {
                            continue;
                        }

                        long size = DirectorySize(sub.FullName, cancellationToken);
                        result.Artifacts.Add(new DevArtifact(sub.FullName, matched.Kind, size, dir));
                        result.TotalBytes += size;
                        progress?.Report(new DevArtifactProgress(result.DirectoriesScanned, result.Artifacts.Count, sub.FullName));

                        // Never descend into a matched artifact (don't find nested node_modules etc.).
                        continue;
                    }

                    pending.Push(sub.FullName);
                }
            }

            result.Artifacts.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            result.Duration = stopwatch.Elapsed;
            return result;
        }, cancellationToken);

    /// <summary>
    /// Re-verifies that a path still is a recognizable dev artifact (right name AND the
    /// project manifest/pyvenv marker still present) and still is NOT inside a runtime
    /// app location. Used by the deleter immediately before recycling as a TOCTOU
    /// defense — if the project was deleted or renamed since the scan, the folder no
    /// longer qualifies and is refused.
    /// </summary>
    public static bool IsArtifact(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            return MatchRule(dir) is not null && !IsRuntimeAppLocation(dir.FullName);
        }
        catch (Exception)
        {
            return false; // fail closed
        }
    }

    // -------------------------------------------------------------------------
    // Runtime-app refusal: installed software looks exactly like a dev project
    // -------------------------------------------------------------------------

    private static readonly string[] RuntimeRootPrefixes = BuildRuntimeRootPrefixes();

    private static string[] BuildRuntimeRootPrefixes()
    {
        var roots = new List<string>();

        void Add(Environment.SpecialFolder folder)
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
        }

        // The entire AppData tree (Local incl. Programs, Roaming, LocalLow): this is where
        // apps LIVE (Electron installs, per-user tools, extension hosts), not where people
        // develop. BitBroom's clean categories handle caches here; dev-junk must not.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(profile, "AppData"))));
        }

        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData); // ProgramData
        Add(Environment.SpecialFolder.Windows);
        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>File names that mark a directory tree as an installed/deployed application.</summary>
    private static readonly string[] RuntimeMarkerFiles =
    [
        "icudtl.dat",                // Electron runtime data — sits next to the app exe
        "v8_context_snapshot.bin",   // Electron/Chromium snapshot
        "Update.exe",                // Squirrel.Windows installer stub (Discord, Slack, …)
        "chrome_100_percent.pak",    // Chromium resources
    ];

    /// <summary>
    /// True when the path belongs to installed/deployed software rather than a development
    /// workspace: anywhere under AppData / Program Files / ProgramData / Windows, inside a
    /// dot-directory directly under the user profile (.cursor, .vscode, .nuget, …), or in a
    /// tree whose ancestors carry app-runtime markers (Electron/Squirrel files, *.asar).
    /// Fails closed on I/O errors.
    /// </summary>
    public static bool IsRuntimeAppLocation(string path)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            // %TEMP% technically lives inside AppData but holds no installed software —
            // it stays scannable (marker detection below still applies inside it).
            if (!IsUnderTemp(full))
            {
                foreach (string root in RuntimeRootPrefixes)
                {
                    if (IsUnder(full, root))
                    {
                        return true;
                    }
                }
            }

            // Dot-directories directly under the profile are tool-managed runtime state
            // (.cursor extensions, .vscode, .nuget, .gradle global cache, …), not projects.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                string profileFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
                if (IsUnder(full, profileFull) && full.Length > profileFull.Length + 1)
                {
                    string firstSegment = full[(profileFull.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
                    if (firstSegment.StartsWith('.'))
                    {
                        return true;
                    }
                }
            }

            // Ancestor walk: an Electron/Squirrel/portable app anywhere (Desktop, D:\Apps…)
            // reveals itself by runtime files next to or above the "project".
            var ancestor = new DirectoryInfo(full).Parent;
            for (int depth = 0; ancestor is not null && depth < 48; depth++, ancestor = ancestor.Parent)
            {
                if (!ancestor.Exists)
                {
                    continue; // non-existent is not evidence either way
                }

                foreach (string marker in RuntimeMarkerFiles)
                {
                    if (File.Exists(Path.Combine(ancestor.FullName, marker)))
                    {
                        return true;
                    }
                }

                try
                {
                    if (ancestor.EnumerateFiles("*.asar").Any())
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    return true; // unreadable ancestor — fail closed
                }
            }

            return false;
        }
        catch (Exception)
        {
            return true; // fail closed
        }
    }

    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length &&
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        path[root.Length] == Path.DirectorySeparatorChar;

    /// <summary>
    /// Cheap per-directory prune used during traversal: is this directory itself the
    /// boundary of runtime-app territory (a runtime root like AppData/Program Files, or a
    /// dot-folder directly under the user profile)?
    /// </summary>
    private static bool IsRuntimeBoundary(string fullPath)
    {
        string full = Path.TrimEndingDirectorySeparator(fullPath);

        if (IsUnderTemp(full))
        {
            return false;
        }

        foreach (string root in RuntimeRootPrefixes)
        {
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || IsUnder(full, root))
            {
                return true;
            }
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            string profileFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
            if (IsUnder(full, profileFull))
            {
                string firstSegment = full[(profileFull.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
                if (firstSegment.StartsWith('.'))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly string TempRoot =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

    private static bool IsUnderTemp(string full) =>
        string.Equals(full, TempRoot, StringComparison.OrdinalIgnoreCase) || IsUnder(full, TempRoot);

    /// <summary>True when the directory itself contains app-runtime marker files.</summary>
    private static bool DirectoryHasRuntimeMarkers(string dir)
    {
        try
        {
            foreach (string marker in RuntimeMarkerFiles)
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
            return true; // unreadable — fail closed
        }
    }

    private static Rule? MatchRule(DirectoryInfo dir)
    {
        string name = dir.Name;
        string parent = dir.Parent?.FullName ?? string.Empty;

        foreach (Rule rule in Rules)
        {
            if (!string.Equals(name, rule.DirName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Python venvs are identified by their own pyvenv.cfg, not a sibling manifest.
            if (rule.DirName is ".venv" or "venv" or "env")
            {
                if (File.Exists(Path.Combine(dir.FullName, "pyvenv.cfg")))
                {
                    return rule;
                }

                continue;
            }

            if (rule.ParentQualifies(parent))
            {
                return rule;
            }
        }

        return null;
    }

    private static long DirectorySize(string path, CancellationToken ct)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();

            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        try
                        {
                            total += file.Length;
                        }
                        catch (Exception)
                        {
                            // Skip unreadable file.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Skip unreadable directory.
            }
        }

        return total;
    }
}
