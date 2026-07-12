using System.Runtime.InteropServices;
using BitBroom.Core.Logging;

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

    private readonly PathGuard _guard;
    private readonly RunLogger _logger;
    private readonly bool _simulate;

    public SafeDeleter(PathGuard guard, RunLogger logger, bool simulate)
    {
        _guard = guard;
        _logger = logger;
        _simulate = simulate;
    }

    public DeleteOutcome DeleteFile(in ScanItem item)
    {
        string? rejection = _guard.ValidateDeletePath(item.Path, item.RootPath);
        if (rejection is not null)
        {
            _logger.Skipped(item.Path, $"guard:{rejection}");
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

        if (_simulate)
        {
            _logger.Deleted(item.Path, item.SizeBytes, simulated: true);
            return DeleteOutcome.Simulated;
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

    /// <summary>
    /// Removes now-empty directories left behind after file deletion, deepest first.
    /// The rule root itself is never removed. Non-empty and in-use directories are ignored.
    /// </summary>
    public int RemoveEmptyDirectories(IReadOnlyList<string> directories, string rootPath)
    {
        if (_simulate)
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
