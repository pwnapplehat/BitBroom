using BitBroom.Core.Util;

namespace BitBroom.Core.Special;

/// <summary>
/// One-shot maintenance actions surfaced in the Tools tab. Every action uses the
/// documented, supported mechanism (DISM, powercfg, ipconfig, vssadmin) and streams
/// its console output back to the UI.
/// </summary>
public static class SystemTools
{
    public static Task<ProcessResult> FlushDnsAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(30), onLine, ct);

    public static Task<ProcessResult> AnalyzeComponentStoreAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /AnalyzeComponentStore", TimeSpan.FromMinutes(15), onLine, ct);

    /// <summary>
    /// Microsoft's supported WinSxS cleanup. Deliberately WITHOUT /ResetBase — that switch
    /// permanently removes the ability to uninstall current updates and is not worth the
    /// extra space for most users (see docs/RESEARCH.md).
    /// </summary>
    public static Task<ProcessResult> ComponentStoreCleanupAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", TimeSpan.FromMinutes(60), onLine, ct);

    public static Task<ProcessResult> DisableHibernationAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("powercfg.exe", "/hibernate off", TimeSpan.FromSeconds(30), onLine, ct);

    public static Task<ProcessResult> EnableHibernationAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("powercfg.exe", "/hibernate on", TimeSpan.FromSeconds(30), onLine, ct);

    /// <summary>Keeps Fast Startup but shrinks hiberfil.sys to ~20% of RAM.</summary>
    public static async Task<ProcessResult> ReduceHibernationFileAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        ProcessResult size = await ProcessRunner.RunAsync("powercfg.exe", "/hibernate /size 0", TimeSpan.FromSeconds(30), onLine, ct)
            .ConfigureAwait(false);
        if (!size.Success)
        {
            return size;
        }

        return await ProcessRunner.RunAsync("powercfg.exe", "/hibernate /type reduced", TimeSpan.FromSeconds(30), onLine, ct)
            .ConfigureAwait(false);
    }

    public static Task<ProcessResult> ListShadowStorageAsync(Action<string>? onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync("vssadmin.exe", "list shadowstorage", TimeSpan.FromSeconds(60), onLine, ct);

    /// <summary>Restarts Explorer to release thumbnail/icon cache locks.</summary>
    public static async Task<ProcessResult> RestartExplorerAsync(Action<string>? onLine, CancellationToken ct = default)
    {
        onLine?.Invoke("Stopping Explorer…");
        ProcessResult kill = await ProcessRunner.RunAsync("taskkill.exe", "/f /im explorer.exe", TimeSpan.FromSeconds(30), onLine, ct)
            .ConfigureAwait(false);

        await Task.Delay(500, ct).ConfigureAwait(false);
        onLine?.Invoke("Starting Explorer…");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, kill.Output, $"Explorer restart failed: {ex.Message}");
        }

        onLine?.Invoke("Explorer restarted.");
        return new ProcessResult(0, kill.Output, string.Empty);
    }

    /// <summary>
    /// Best-effort restore point creation via PowerShell Checkpoint-Computer.
    /// Windows rate-limits creation (default: one per 24h) — a skip is not an error.
    /// </summary>
    public static async Task<(bool Created, string Message)> TryCreateRestorePointAsync(CancellationToken ct = default)
    {
        if (!ElevationInfo.IsElevated)
        {
            return (false, "Restore point skipped: administrator rights required.");
        }

        ProcessResult result = await ProcessRunner.RunPowerShellAsync(
            "Checkpoint-Computer -Description 'BitBroom before clean' -RestorePointType MODIFY_SETTINGS",
            TimeSpan.FromMinutes(3),
            null,
            ct).ConfigureAwait(false);

        if (result.Success && string.IsNullOrWhiteSpace(result.Error))
        {
            return (true, "Restore point created.");
        }

        string message = result.Error.Trim();
        if (message.Contains("1440", StringComparison.Ordinal) || message.Contains("frequently", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Restore point skipped: Windows already created one in the last 24 hours.");
        }

        if (message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Restore point skipped: System Protection is disabled on this PC.");
        }

        return (false, $"Restore point not created: {(message.Length > 0 ? message : "unknown error")}");
    }
}
