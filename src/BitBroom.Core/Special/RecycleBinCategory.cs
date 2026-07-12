using System.Diagnostics;
using BitBroom.Core.Engine;
using BitBroom.Core.Native;

namespace BitBroom.Core.Special;

/// <summary>
/// Empties the Recycle Bin via the shell API (all drives).
/// Off by default: the bin can contain files the user still wants back.
/// </summary>
public sealed class RecycleBinCategory : CleanCategory
{
    public const string CategoryId = "recycle-bin";

    public static RecycleBinCategory Create() => new()
    {
        Id = CategoryId,
        Name = "Recycle Bin",
        Description = "Permanently empties the Recycle Bin on all drives. Items in the bin can otherwise still be restored.",
        Group = CategoryGroup.System,
        Risk = RiskLevel.Moderate,
        EnabledByDefault = false,
        RequiresAdmin = false,
        Warning = "Emptying the Recycle Bin is permanent. Make sure nothing in it is still needed.",
    };

    public override Task<CategoryScanResult> ScanAsync(ScanContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new CategoryScanResult { CategoryId = Id };
            var stopwatch = Stopwatch.StartNew();

            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>(),
            };

            int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            if (hr == 0 && info.i64NumItems > 0)
            {
                // One virtual item carrying the totals; the real entry count is reported
                // via VirtualItemCount so a multi-million-item bin costs no memory.
                result.TotalBytes = info.i64Size;
                result.VirtualItemCount = (int)Math.Min(info.i64NumItems, int.MaxValue);
                result.Items.Add(new ScanItem(
                    "Recycle Bin",
                    info.i64Size,
                    DateTime.UtcNow,
                    "Recycle Bin",
                    ScanItemFlags.Virtual));
            }
            else if (hr != 0)
            {
                result.Errors.Add($"SHQueryRecycleBin failed (0x{hr:X8}).");
            }

            result.Duration = stopwatch.Elapsed;
            return result;
        }, cancellationToken);
    }

    public override Task<CategoryCleanResult> CleanAsync(
        CleanContext context,
        CategoryScanResult scan,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new CategoryCleanResult { CategoryId = Id, Simulated = context.Simulate };
            var stopwatch = Stopwatch.StartNew();

            if (scan.Items.Count == 0)
            {
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            if (context.Simulate)
            {
                context.Logger.Deleted("Recycle Bin (all drives)", scan.TotalBytes, simulated: true);
                result.Deleted = scan.FileCount;
                result.BytesFreed = scan.TotalBytes;
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            int hr = NativeMethods.SHEmptyRecycleBin(
                IntPtr.Zero,
                null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

            if (hr == 0)
            {
                context.Logger.Deleted("Recycle Bin (all drives)", scan.TotalBytes, simulated: false);
                result.Deleted = scan.FileCount;
                result.BytesFreed = scan.TotalBytes;
            }
            else
            {
                result.Errors.Add($"SHEmptyRecycleBin failed (0x{hr:X8}).");
                context.Logger.Error($"SHEmptyRecycleBin failed (0x{hr:X8}).");
            }

            result.Duration = stopwatch.Elapsed;
            return result;
        }, cancellationToken);
    }
}
