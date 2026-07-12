using System.Diagnostics;

namespace BitBroom.Core.Tests;

/// <summary>
/// A disposable sandbox directory under %TEMP%\BitBroomTests. All file-system tests
/// operate exclusively inside sandboxes — never on real system locations.
/// </summary>
public sealed class TestSandbox : IDisposable
{
    public string Root { get; }

    public TestSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "BitBroomTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string CreateDirectory(params string[] segments)
    {
        string path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, int sizeBytes = 16, DateTime? lastWriteUtc = null)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        if (lastWriteUtc is not null)
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc.Value);
            File.SetCreationTimeUtc(path, lastWriteUtc.Value);
        }

        return path;
    }

    /// <summary>Creates an NTFS junction (no admin required). Returns false when unavailable.</summary>
    public static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? process = Process.Start(psi);
            process!.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(junctionPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so the recursive delete cannot traverse them.
            foreach (string dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    info.Delete();
                }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Leftover sandboxes in %TEMP% are harmless (and BitBroom can clean them, ha).
        }
    }
}
