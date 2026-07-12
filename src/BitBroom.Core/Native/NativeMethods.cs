using System.Runtime.InteropServices;

namespace BitBroom.Core.Native;

/// <summary>
/// P/Invoke declarations for shell operations that have no managed equivalent.
/// x64/arm64 only (BitBroom does not ship 32-bit builds), so no packing shims are needed.
/// </summary>
public static class NativeMethods
{
    // ---------------------------------------------------------------------
    // Recycle Bin
    // ---------------------------------------------------------------------

    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI = 0x00000002;
    public const uint SHERB_NOSOUND = 0x00000004;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // ---------------------------------------------------------------------
    // SHFileOperation — used only for "send to Recycle Bin" (FOF_ALLOWUNDO)
    // from the Disk Analyzer, never for cleaning-category deletions.
    // ---------------------------------------------------------------------

    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Sends a file or directory to the Recycle Bin. Returns 0 on success.</summary>
    public static int SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            // pFrom must be double-null-terminated; the marshaller adds the final null.
            pFrom = path + "\0",
            pTo = null,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        return SHFileOperation(ref op);
    }

    // ---------------------------------------------------------------------
    // Known folders (Downloads has no Environment.SpecialFolder entry)
    // ---------------------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? GetDownloadsFolderPath()
    {
        IntPtr pathPtr = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out pathPtr);
            return hr == 0 ? Marshal.PtrToStringUni(pathPtr) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (pathPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }

    // ---------------------------------------------------------------------
    // File attribute constants missing from System.IO.FileAttributes docs use
    // ---------------------------------------------------------------------

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN — cloud placeholder, hydrates when opened.</summary>
    public const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS — cloud placeholder (OneDrive Files On-Demand).</summary>
    public const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
}
