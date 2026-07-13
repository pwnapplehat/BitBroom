using BitBroom.Core.Engine;
using BitBroom.Core.Logging;
using BitBroom.Core.Native;

namespace BitBroom.Core.Dupes;

public sealed class DuplicateDeleteResult
{
    public int Recycled { get; set; }
    public long BytesRecycled { get; set; }
    public int Failed { get; set; }
    public int RefusedByGuard { get; set; }

    /// <summary>Groups skipped because the selection would have deleted every copy.</summary>
    public int GroupsRefusedKeepOne { get; set; }

    public List<string> Errors { get; } = [];
}

/// <summary>
/// Deletes user-selected duplicate files and empty folders. Deliberately conservative:
/// everything goes to the Recycle Bin (never permanent), at least one file of every
/// duplicate group must survive (enforced here, not just in the UI), and protected
/// locations (Windows, Program Files, drive roots) are refused outright. Every action
/// lands in the audit log.
/// </summary>
public sealed class DuplicateDeleter
{
    private readonly RunLogger _logger;

    /// <summary>Subtrees no duplicate/empty-folder deletion may ever touch.</summary>
    private static readonly string[] ForbiddenSubtrees = BuildForbidden();

    private static string[] BuildForbidden()
    {
        var list = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                list.Add(PathGuard.Normalize(path));
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        return [.. list];
    }

    public DuplicateDeleter(RunLogger logger)
    {
        _logger = logger;
    }

    public static string? ValidatePath(string path)
    {
        string normalized;
        try
        {
            normalized = PathGuard.Normalize(path);
        }
        catch (Exception)
        {
            return "path is malformed";
        }

        if (PathGuard.IsDriveRoot(normalized))
        {
            return "path is a drive root";
        }

        foreach (string forbidden in ForbiddenSubtrees)
        {
            if (PathGuard.PathsEqual(normalized, forbidden) || PathGuard.IsUnder(normalized, forbidden))
            {
                return "path is inside a protected system location";
            }
        }

        return null;
    }

    /// <summary>
    /// Recycles the selected files of each group. A group whose selection covers every
    /// copy is refused entirely — one copy always survives, regardless of what the
    /// caller passed in.
    /// </summary>
    public DuplicateDeleteResult RecycleSelected(
        IReadOnlyList<(DuplicateGroup Group, IReadOnlyList<DuplicateFile> Selected)> selections,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DuplicateDeleteResult();
        int done = 0;

        foreach ((DuplicateGroup group, IReadOnlyList<DuplicateFile> selected) in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (selected.Count == 0)
            {
                continue;
            }

            if (selected.Count >= group.Files.Count)
            {
                result.GroupsRefusedKeepOne++;
                _logger.Warn($"Refused duplicate group ({group.Files.Count} copies of {group.FileSizeBytes} bytes): selection would delete every copy.");
                continue;
            }

            foreach (DuplicateFile file in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? refusal = ValidatePath(file.Path);
                if (refusal is not null)
                {
                    result.RefusedByGuard++;
                    _logger.Skipped(file.Path, $"guard:{refusal}");
                    continue;
                }

                if (!File.Exists(file.Path))
                {
                    continue;
                }

                int code = NativeMethods.SendToRecycleBin(file.Path);
                if (code == 0 && !File.Exists(file.Path))
                {
                    result.Recycled++;
                    result.BytesRecycled += file.SizeBytes;
                    _logger.Recycled(file.Path, file.SizeBytes);
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"Could not recycle (error {code}): {file.Path}");
                    _logger.Skipped(file.Path, $"recycle:{code}");
                }

                progress?.Report(++done);
            }
        }

        return result;
    }

    /// <summary>Recycles empty folders (deepest first so nested empties fold up).</summary>
    public DuplicateDeleteResult RecycleEmptyFolders(
        IReadOnlyList<string> folders,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DuplicateDeleteResult();
        int done = 0;

        foreach (string folder in folders.OrderByDescending(PathGuard.Depth))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? refusal = ValidatePath(folder);
            if (refusal is not null)
            {
                result.RefusedByGuard++;
                _logger.Skipped(folder, $"guard:{refusal}");
                continue;
            }

            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Already gone (recycled as part of a parent) or a junction — skip silently.
                    continue;
                }

                // Re-verify emptiness right before deletion (TOCTOU defense). A folder whose
                // only content is empty subfolders is still deletable — the recursive check
                // refuses the moment any file or reparse point exists anywhere below.
                if (!IsSubtreeFileFree(info))
                {
                    result.RefusedByGuard++;
                    _logger.Skipped(folder, "no-longer-empty");
                    continue;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Could not inspect {folder}: {ex.Message}");
                continue;
            }

            int code = NativeMethods.SendToRecycleBin(folder);
            if (code == 0 && !Directory.Exists(folder))
            {
                result.Recycled++;
                _logger.Recycled(folder, 0);
            }
            else
            {
                result.Failed++;
                result.Errors.Add($"Could not recycle (error {code}): {folder}");
                _logger.Skipped(folder, $"recycle:{code}");
            }

            progress?.Report(++done);
        }

        return result;
    }

    /// <summary>
    /// True when the directory subtree contains no files and no reparse points anywhere —
    /// i.e. deleting it recursively cannot destroy content. Unreadable entries fail closed.
    /// </summary>
    private static bool IsSubtreeFileFree(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            DirectoryInfo dir = pending.Pop();
            foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
                ReturnSpecialDirectories = false,
            }))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((DirectoryInfo)entry);
                }
                else
                {
                    return false;
                }
            }
        }

        return true;
    }
}
