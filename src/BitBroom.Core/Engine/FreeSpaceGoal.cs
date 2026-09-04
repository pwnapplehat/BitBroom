using BitBroom.Core.Logging;

namespace BitBroom.Core.Engine;

/// <summary>
/// Stop-when-enough policy for <c>bitbroom-cli clean --until-free</c>.
/// Counts space on one drive only; files on other volumes are left alone.
/// All-or-nothing categories (Windows.old, Recycle Bin, event logs, Delivery
/// Optimization) run only when their whole size fits in the remaining deficit,
/// so a 20 GB rollback folder is never deleted to free 2 GB.
/// </summary>
public sealed class FreeSpaceGoal
{
    private readonly Func<long>? _liveAvailable;
    private long _accountedFreed;

    public string DriveRoot { get; }
    public long TargetFreeBytes { get; }
    public long InitialAvailableBytes { get; }

    /// <summary>
    /// Accounting plus a live <see cref="DriveInfo"/> read when one was provided.
    /// Live reads can lag NTFS; taking the max keeps us from overshooting because
    /// the OS has not refreshed yet, and from stopping early because accounting
    /// ignored cluster slack the OS already returned.
    /// </summary>
    public long EffectiveFreeBytes
    {
        get
        {
            long accounted = InitialAvailableBytes + _accountedFreed;
            if (_liveAvailable is null)
            {
                return accounted;
            }

            long live;
            try
            {
                live = _liveAvailable();
            }
            catch (Exception)
            {
                return accounted;
            }

            return Math.Max(accounted, live);
        }
    }

    public long RemainingBytes => Math.Max(0L, TargetFreeBytes - EffectiveFreeBytes);

    public bool IsSatisfied => RemainingBytes == 0;

    public FreeSpaceGoal(string driveRoot, long targetFreeBytes, long initialAvailableBytes, Func<long>? liveAvailable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        if (targetFreeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFreeBytes), "Target free space must be positive.");
        }

        if (initialAvailableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialAvailableBytes));
        }

        DriveRoot = NormalizeRoot(driveRoot);
        TargetFreeBytes = targetFreeBytes;
        InitialAvailableBytes = initialAvailableBytes;
        _liveAvailable = liveAvailable;
    }

    /// <summary>Live goal against a ready Windows volume. Throws if the drive is missing or not ready.</summary>
    public static FreeSpaceGoal ForLiveDrive(string driveRoot, long targetFreeBytes)
    {
        string root = NormalizeRoot(driveRoot);
        DriveInfo info;
        try
        {
            info = new DriveInfo(root);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Drive '{root}' is not available: {ex.Message}");
        }

        if (!info.IsReady)
        {
            throw new InvalidOperationException($"Drive '{root}' is not ready.");
        }

        string resolved = info.RootDirectory.FullName;
        long initial = info.AvailableFreeSpace;
        return new FreeSpaceGoal(resolved, targetFreeBytes, initial, () => new DriveInfo(resolved).AvailableFreeSpace);
    }

    public static string SystemDriveRoot()
    {
        string? root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetPathRoot(Environment.SystemDirectory);
        }

        return NormalizeRoot(root ?? @"C:\");
    }

    public static bool TryParseDrive(string? input, out string root, out string error)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Drive value is empty.";
            return false;
        }

        string trimmed = input.Trim();
        try
        {
            if (trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            {
                string? parsed = Path.GetPathRoot(Path.GetFullPath(trimmed.Length == 2 ? trimmed + "\\" : trimmed));
                if (!string.IsNullOrEmpty(parsed))
                {
                    root = NormalizeRoot(parsed);
                    error = string.Empty;
                    return true;
                }
            }
            else if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0]))
            {
                root = NormalizeRoot(char.ToUpperInvariant(trimmed[0]) + ":\\");
                error = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            error = $"Invalid drive '{input}': {ex.Message}";
            return false;
        }

        error = $"Invalid drive '{input}'. Use a drive letter, e.g. C: or D.";
        return false;
    }

    public bool IsOnDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf(':') < 0)
        {
            return false;
        }

        try
        {
            string? itemRoot = Path.GetPathRoot(Path.GetFullPath(path));
            return itemRoot is not null && PathGuard.PathsEqual(itemRoot, DriveRoot);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an all-or-nothing category should be skipped. A null
    /// <paramref name="representativePath"/> means the operation spans every drive
    /// (Recycle Bin) and is not filtered by volume.
    /// </summary>
    public bool ShouldSkipAtomic(long estimatedBytes, string? representativePath)
    {
        if (IsSatisfied)
        {
            return true;
        }

        if (representativePath is not null && !IsOnDrive(representativePath))
        {
            return true;
        }

        return estimatedBytes <= 0 || estimatedBytes > RemainingBytes;
    }

    public bool ShouldSkipItem(in ScanItem item)
        => IsSatisfied || !IsOnDrive(item.Path);

    public void NoteFreed(long bytes)
    {
        if (bytes > 0)
        {
            _accountedFreed += bytes;
        }
    }

    public static string NormalizeRoot(string driveRoot)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(driveRoot));
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"'{driveRoot}' is not a drive root.", nameof(driveRoot));
        }

        return PathGuard.Normalize(root);
    }
}

/// <summary>Shared until-free checks for rule-based and special categories.</summary>
public static class FreeSpaceGoalExtensions
{
    /// <returns>True when the caller should return an empty result without doing work.</returns>
    public static bool SkipAtomicCategory(
        this CleanContext context,
        string categoryId,
        long estimatedBytes,
        string? representativePath)
    {
        if (context.Goal is null || !context.Goal.ShouldSkipAtomic(estimatedBytes, representativePath))
        {
            return false;
        }

        context.Logger.Info(
            $"Skipping '{categoryId}': --until-free target already met, or this all-or-nothing category is larger than the remaining deficit on {context.Goal.DriveRoot}.");
        return true;
    }

    public static void NoteGoalFreed(this CleanContext context, long bytes)
        => context.Goal?.NoteFreed(bytes);
}
