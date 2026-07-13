using System.Runtime.InteropServices;
using BitBroom.Core.Logging;
using BitBroom.Core.Native;

namespace BitBroom.Core.Engine;

public enum DeleteOutcome
{
    Deleted,
    Simulated,
    Locked,
    AccessDenied,
    Missing,
    GuardRejected,
    Failed,
}

/// <summary>How the deleter disposes of files.</summary>
public enum DeleteMode
{
    /// <summary>Log what would be deleted; touch nothing.</summary>
    Simulate,

    /// <summary>Send files to the Recycle Bin (restorable until the bin is emptied).</summary>
    RecycleBin,

    /// <summary>Delete permanently.</summary>
    Permanent,
}

/// <summary>
/// The only code path in BitBroom that deletes files for cleaning categories.
/// Every deletion re-validates the path against its scanned root and re-reads
/// attributes so reparse points and cloud placeholders are refused even if they
/// appeared after scanning (TOCTOU defense).
/// </summary>
public sealed class SafeDeleter
{
    private const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
    private const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);

    // SHFileOperation result codes worth mapping (WinError/DE_* values).
    private const int SH_ERROR_ACCESS_DENIED = 5;
    private const int SH_ERROR_SHARING_VIOLATION = 32;
    private const int SH_ERROR_FILE_NOT_FOUND = 2;
    private const int SH_ERROR_PATH_NOT_FOUND = 3;

    private readonly PathGuard _guard;
    private readonly RunLogger _logger;
    private readonly DeleteMode _mode;
    private readonly ExclusionSet _exclusions;

    public SafeDeleter(PathGuard guard, RunLogger logger, bool simulate)
        : this(guard, logger, simulate ? DeleteMode.Simulate : DeleteMode.Permanent, ExclusionSet.Empty)
    {
    }

    public SafeDeleter(PathGuard guard, RunLogger logger, DeleteMode mode, ExclusionSet? exclusions = null)
    {
        _guard = guard;
        _logger = logger;
        _mode = mode;
        _exclusions = exclusions ?? ExclusionSet.Empty;
    }

    private bool Simulating => _mode == DeleteMode.Simulate;

    public DeleteOutcome DeleteFile(in ScanItem item)
    {
        string? rejection = _guard.ValidateDeletePath(item.Path, item.RootPath);
        if (rejection is not null)
        {
            _logger.Skipped(item.Path, $"guard:{rejection}");
            return DeleteOutcome.GuardRejected;
        }

        if (_exclusions.Count > 0 && _exclusions.IsExcluded(item.Path))
        {
            _logger.Skipped(item.Path, "excluded");
            return DeleteOutcome.GuardRejected;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(item.Path);
        }
        catch (FileNotFoundException)
        {
            return DeleteOutcome.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return DeleteOutcome.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Skipped(item.Path, "denied");
            return DeleteOutcome.AccessDenied;
        }
        catch (IOException)
        {
            _logger.Skipped(item.Path, "io-error");
            return DeleteOutcome.Failed;
        }

        string? attributeIssue = PathGuard.ValidateDeletableAttributes(attributes);
        if (attributeIssue is not null)
        {
            _logger.Skipped(item.Path, $"guard:{attributeIssue}");
            return DeleteOutcome.GuardRejected;
        }

        if (Simulating)
        {
            _logger.Deleted(item.Path, item.SizeBytes, simulated: true);
            return DeleteOutcome.Simulated;
        }

        if (_mode == DeleteMode.RecycleBin)
        {
            return RecycleFile(item, attributes);
        }

        try
        {
            File.Delete(item.Path);
            _logger.Deleted(item.Path, item.SizeBytes, simulated: false);
            return DeleteOutcome.Deleted;
        }
        catch (FileNotFoundException)
        {
            return DeleteOutcome.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return DeleteOutcome.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only files throw UnauthorizedAccess; clear the attribute and retry once.
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                try
                {
                    File.SetAttributes(item.Path, FileAttributes.Normal);
                    File.Delete(item.Path);
                    _logger.Deleted(item.Path, item.SizeBytes, simulated: false);
                    return DeleteOutcome.Deleted;
                }
                catch (Exception)
                {
                    // fall through to denied
                }
            }

            _logger.Skipped(item.Path, "denied");
            return DeleteOutcome.AccessDenied;
        }
        catch (IOException ex) when (ex.HResult is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION)
        {
            _logger.Skipped(item.Path, "in-use");
            return DeleteOutcome.Locked;
        }
        catch (IOException ex)
        {
            _logger.Skipped(item.Path, $"io:{Marshal.GetHRForException(ex):x8}");
            return DeleteOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected error deleting {item.Path}: {ex.Message}");
            return DeleteOutcome.Failed;
        }
    }

    /// <summary>Sends one validated file to the Recycle Bin, mapping shell error codes.</summary>
    private DeleteOutcome RecycleFile(in ScanItem item, FileAttributes attributes)
    {
        try
        {
            // The shell refuses read-only files in some code paths; normalize first
            // (the file is going to the bin anyway, restore keeps content intact).
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                try
                {
                    File.SetAttributes(item.Path, FileAttributes.Normal);
                }
                catch (Exception)
                {
                    // Recycle may still succeed; fall through.
                }
            }

            int code = NativeMethods.SendToRecycleBin(item.Path);
            switch (code)
            {
                case 0:
                    _logger.Recycled(item.Path, item.SizeBytes);
                    return DeleteOutcome.Deleted;
                case SH_ERROR_FILE_NOT_FOUND:
                case SH_ERROR_PATH_NOT_FOUND:
                    return DeleteOutcome.Missing;
                case SH_ERROR_SHARING_VIOLATION:
                    _logger.Skipped(item.Path, "in-use");
                    return DeleteOutcome.Locked;
                case SH_ERROR_ACCESS_DENIED:
                    _logger.Skipped(item.Path, "denied");
                    return DeleteOutcome.AccessDenied;
                default:
                    _logger.Skipped(item.Path, $"recycle:{code}");
                    return DeleteOutcome.Failed;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected error recycling {item.Path}: {ex.Message}");
            return DeleteOutcome.Failed;
        }
    }

    /// <summary>
    /// Removes now-empty directories left behind after file deletion, deepest first.
    /// The rule root itself is never removed. Non-empty and in-use directories are ignored.
    /// </summary>
    public int RemoveEmptyDirectories(IReadOnlyList<string> directories, string rootPath)
    {
        if (Simulating)
        {
            return 0;
        }

        int removed = 0;
        foreach (string dir in directories.OrderByDescending(PathGuard.Depth))
        {
            if (PathGuard.PathsEqual(dir, rootPath) || !PathGuard.IsUnder(dir, rootPath))
            {
                continue;
            }

            if (_guard.ValidateDeletePath(dir, rootPath) is not null)
            {
                continue;
            }

            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                // Non-recursive delete: only succeeds when truly empty.
                info.Delete(recursive: false);
                removed++;
            }
            catch (Exception)
            {
                // Directory not empty, locked, or already gone — all fine.
            }
        }

        return removed;
    }
}
