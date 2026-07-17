using BitBroom.Core.Special;
using Xunit;

namespace BitBroom.Core.Tests;

/// <summary>
/// Guards the Docker-lock handling and console-output hygiene of the WSL/Docker disk
/// compaction tool: Docker Desktop keeps its vhdx files attached while it runs (diskpart
/// then fails with "file in use"), so those disks must be classified correctly and
/// skipped; and diskpart's repainted progress lines must be deduplicated.
/// </summary>
public class VhdxCompactionTests
{
    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Docker\wsl\disk\docker_data.vhdx")]
    [InlineData(@"C:\Users\u\AppData\Local\Docker\wsl\main\ext4.vhdx")]
    [InlineData(@"C:\Users\u\AppData\Local\Docker\wsl\data\ext4.vhdx")]
    [InlineData("C:/Users/u/AppData/Local/Docker/wsl/data/ext4.vhdx")] // forward slashes
    [InlineData(@"C:\Users\u\AppData\Local\Packages\DockerDesktopWSL_abc\LocalState\ext4.vhdx")]
    public void Docker_owned_disks_are_classified_as_docker(string path)
        => Assert.True(SystemTools.IsDockerDiskPath(path));

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState\ext4.vhdx")]
    [InlineData(@"C:\Users\u\AppData\Local\wsl\{guid}\ext4.vhdx")]
    [InlineData(@"D:\vms\dev-machine.vhdx")]
    public void Non_docker_disks_are_not_classified_as_docker(string path)
        => Assert.False(SystemTools.IsDockerDiskPath(path));

    [Fact]
    public void Repeated_progress_lines_are_collapsed_to_one_per_percentage()
    {
        var seen = new List<string>();
        Action<string> filtered = SystemTools.FilterDiskpartProgress(seen.Add)!;

        foreach (string line in new[]
        {
            "  0 percent completed",
            " 19 percent completed",
            " 19 percent completed",
            " 19 percent completed",
            " 20 percent completed",
            " 20 percent completed",
            " 89 percent completed",
            "100 percent completed",
        })
        {
            filtered(line);
        }

        Assert.Equal(
            ["  0 percent completed", " 19 percent completed", " 20 percent completed", " 89 percent completed", "100 percent completed"],
            seen);
    }

    [Fact]
    public void Non_progress_lines_always_pass_and_reset_the_dedup()
    {
        var seen = new List<string>();
        Action<string> filtered = SystemTools.FilterDiskpartProgress(seen.Add)!;

        filtered(" 50 percent completed");
        filtered("DiskPart successfully compacted the virtual disk file.");
        filtered("DiskPart successfully compacted the virtual disk file."); // repeated info line: kept (only progress dedups)
        filtered(" 50 percent completed"); // new compaction pass — same % must show again

        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void Blank_lines_pass_through_without_resetting_progress_dedup()
    {
        var seen = new List<string>();
        Action<string> filtered = SystemTools.FilterDiskpartProgress(seen.Add)!;

        filtered(" 42 percent completed");
        filtered("");
        filtered(" 42 percent completed"); // still the same repaint — keep suppressed

        Assert.Equal([" 42 percent completed", ""], seen);
    }

    [Fact]
    public void Null_sink_stays_null()
        => Assert.Null(SystemTools.FilterDiskpartProgress(null));
}
