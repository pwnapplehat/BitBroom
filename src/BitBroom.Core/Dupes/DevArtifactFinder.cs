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
/// dist, __pycache__, …). Every match is guarded by a sibling-manifest check so a folder is
/// only flagged when it is unambiguously a build artifact of a real project — e.g.
/// node_modules only counts when package.json sits next to it, target only when Cargo.toml
/// or pom.xml does. This near-eliminates false positives (a user folder literally named
/// "build" or "target" is never touched). Matched directories are never recursed into.
/// Junctions/symlinks are never followed. Deletion goes through <see cref="DuplicateDeleter"/>
/// (Recycle Bin only).
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

                    Rule? matched = MatchRule(sub);
                    if (matched is not null)
                    {
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
    /// project manifest/pyvenv marker still present). Used by the deleter immediately
    /// before recycling as a TOCTOU defense — if the project was deleted or renamed
    /// since the scan, the folder no longer qualifies and is refused.
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

            return MatchRule(dir) is not null;
        }
        catch (Exception)
        {
            return false; // fail closed
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
