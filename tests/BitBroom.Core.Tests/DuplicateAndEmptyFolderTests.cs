using BitBroom.Core.Dupes;
using BitBroom.Core.Engine;
using BitBroom.Core.Logging;
using Xunit;

namespace BitBroom.Core.Tests;

public class DuplicateAndEmptyFolderTests
{
    private static RunLogger NullLogger() => new(Path.Combine(Path.GetTempPath(), "BitBroomTests", "logs"), "test");

    private static string WriteFile(TestSandbox sandbox, string relative, byte[] content)
    {
        string path = Path.Combine(sandbox.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Pattern(byte seed, int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)(seed + i % 251);
        }

        return data;
    }

    // =========================================================================
    // DuplicateFinder
    // =========================================================================

    [Fact]
    public async Task Finds_content_identical_files_across_nested_dirs()
    {
        using var sandbox = new TestSandbox();
        byte[] content = Pattern(1, 4096);
        string a = WriteFile(sandbox, "a\\one.bin", content);
        string b = WriteFile(sandbox, "b\\deep\\two.bin", content);
        WriteFile(sandbox, "c\\unique.bin", Pattern(2, 4096));

        var finder = new DuplicateFinder();
        DuplicateScanResult result = await finder.ScanAsync(sandbox.Root, minFileSizeBytes: 1);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(4096, group.FileSizeBytes);
        Assert.Equal(4096, group.WastedBytes);
        Assert.Contains(group.Files, f => PathGuard.PathsEqual(f.Path, a));
        Assert.Contains(group.Files, f => PathGuard.PathsEqual(f.Path, b));
    }

    [Fact]
    public async Task Same_size_different_content_is_not_a_duplicate()
    {
        using var sandbox = new TestSandbox();
        WriteFile(sandbox, "x.bin", Pattern(1, 2048));
        WriteFile(sandbox, "y.bin", Pattern(9, 2048));

        var finder = new DuplicateFinder();
        DuplicateScanResult result = await finder.ScanAsync(sandbox.Root, minFileSizeBytes: 1);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Same_head_different_tail_is_distinguished()
    {
        // Both files share the first 256 KB (bigger than the 128 KB head-hash window)
        // but diverge at the end — the full-hash stage must split them.
        using var sandbox = new TestSandbox();
        int size = 300 * 1024;
        byte[] one = Pattern(1, size);
        byte[] two = Pattern(1, size);
        two[size - 1] ^= 0xFF;

        WriteFile(sandbox, "one.bin", one);
        WriteFile(sandbox, "two.bin", two);

        var finder = new DuplicateFinder();
        DuplicateScanResult result = await finder.ScanAsync(sandbox.Root, minFileSizeBytes: 1);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Min_size_filter_and_junctions_are_respected()
    {
        using var sandbox = new TestSandbox();
        byte[] small = Pattern(3, 128);
        WriteFile(sandbox, "s1.bin", small);
        WriteFile(sandbox, "s2.bin", small);

        string outside = sandbox.CreateDirectory("outside");
        byte[] big = Pattern(4, 8192);
        WriteFile(sandbox, "outside\\big1.bin", big);
        string scanRoot = sandbox.CreateDirectory("scan");
        WriteFile(sandbox, "scan\\big2.bin", big);

        bool junctionMade = TestSandbox.TryCreateJunction(Path.Combine(scanRoot, "link"), outside);

        var finder = new DuplicateFinder();
        DuplicateScanResult result = await finder.ScanAsync(sandbox.Root, minFileSizeBytes: 1024);

        // Small pair filtered by min size. big1/big2 ARE duplicates via the real paths,
        // but the junction inside scan/ must never be traversed (no triple counting).
        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        if (junctionMade)
        {
            Assert.True(result.SkippedReparsePoints >= 1);
        }
    }

    // =========================================================================
    // DuplicateDeleter — keep-one enforcement and guard refusals
    // =========================================================================

    [Fact]
    public void Selecting_every_copy_refuses_the_whole_group()
    {
        using var sandbox = new TestSandbox();
        byte[] content = Pattern(5, 1024);
        string a = WriteFile(sandbox, "a.bin", content);
        string b = WriteFile(sandbox, "b.bin", content);

        var group = new DuplicateGroup
        {
            ContentHash = "test",
            FileSizeBytes = 1024,
            Files =
            [
                new DuplicateFile(a, 1024, DateTime.UtcNow),
                new DuplicateFile(b, 1024, DateTime.UtcNow),
            ],
        };

        using RunLogger logger = NullLogger();
        var deleter = new DuplicateDeleter(logger);
        DuplicateDeleteResult result = deleter.RecycleSelected([(group, group.Files)]);

        Assert.Equal(1, result.GroupsRefusedKeepOne);
        Assert.Equal(0, result.Recycled);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Protected_locations_are_refused_for_duplicate_deletion()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(DuplicateDeleter.ValidatePath(Path.Combine(windows, "notepad.exe")));
        Assert.NotNull(DuplicateDeleter.ValidatePath(@"C:\"));
        Assert.Null(DuplicateDeleter.ValidatePath(Path.Combine(Path.GetTempPath(), "whatever.bin")));
    }

    [SkippableRecycleFact]
    public void Recycle_selected_keeps_the_survivor()
    {
        using var sandbox = new TestSandbox();
        byte[] content = Pattern(6, 1024);
        string keep = WriteFile(sandbox, "keep.bin", content);
        string toss = WriteFile(sandbox, "toss.bin", content);

        var group = new DuplicateGroup
        {
            ContentHash = "test",
            FileSizeBytes = 1024,
            Files =
            [
                new DuplicateFile(keep, 1024, DateTime.UtcNow),
                new DuplicateFile(toss, 1024, DateTime.UtcNow),
            ],
        };

        using RunLogger logger = NullLogger();
        var deleter = new DuplicateDeleter(logger);
        DuplicateDeleteResult result = deleter.RecycleSelected(
            [(group, new List<DuplicateFile> { group.Files.First(f => PathGuard.PathsEqual(f.Path, toss)) })]);

        Assert.Equal(1, result.Recycled);
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(toss));
    }

    // =========================================================================
    // EmptyFolderFinder
    // =========================================================================

    [Fact]
    public async Task Finds_empty_and_effectively_empty_folders()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateDirectory("empty1");
        sandbox.CreateDirectory("parent", "childEmpty");
        sandbox.CreateFile(@"full\file.txt");
        sandbox.CreateFile(@"mixed\data.txt");
        sandbox.CreateDirectory("mixed", "hollow");

        var finder = new EmptyFolderFinder();
        EmptyFolderScanResult result = await finder.ScanAsync(sandbox.Root);

        string[] names = [.. result.EmptyFolders.Select(Path.GetFileName)!];
        Assert.Contains("empty1", names);
        Assert.Contains("childEmpty", names);
        Assert.Contains("parent", names);   // contains only an empty child → effectively empty
        Assert.Contains("hollow", names);
        Assert.DoesNotContain("full", names);
        Assert.DoesNotContain("mixed", names);

        // The scan root itself is never reported.
        Assert.DoesNotContain(result.EmptyFolders, f => PathGuard.PathsEqual(f, sandbox.Root));
    }

    [Fact]
    public async Task Folder_holding_a_junction_is_never_empty()
    {
        using var sandbox = new TestSandbox();
        string precious = sandbox.CreateDirectory("precious");
        sandbox.CreateFile(@"precious\data.txt");
        string holder = sandbox.CreateDirectory("holder");

        if (!TestSandbox.TryCreateJunction(Path.Combine(holder, "link"), precious))
        {
            return;
        }

        var finder = new EmptyFolderFinder();
        EmptyFolderScanResult result = await finder.ScanAsync(sandbox.Root);

        Assert.DoesNotContain(result.EmptyFolders, f => f.EndsWith("holder", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.SkippedReparsePoints >= 1);
    }

    [SkippableRecycleFact]
    public void Nested_empty_folders_recycle_deepest_first()
    {
        using var sandbox = new TestSandbox();
        string parent = sandbox.CreateDirectory("p");
        string child = sandbox.CreateDirectory("p", "c");

        using RunLogger logger = NullLogger();
        var deleter = new DuplicateDeleter(logger);
        DuplicateDeleteResult result = deleter.RecycleEmptyFolders([parent, child]);

        Assert.True(result.Recycled >= 1);
        Assert.False(Directory.Exists(parent));
        Assert.Equal(0, result.Failed);
    }
}

/// <summary>
/// Recycle-Bin tests actually move files into the current user's bin; they are opt-in
/// (BITBROOM_TEST_RECYCLE=1) so contributor machines aren't polluted by default.
/// CI and the maintainer's E2E runs set the variable.
/// </summary>
public sealed class SkippableRecycleFactAttribute : FactAttribute
{
    public SkippableRecycleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BITBROOM_TEST_RECYCLE") != "1")
        {
            Skip = "Set BITBROOM_TEST_RECYCLE=1 to run Recycle Bin integration tests.";
        }
    }
}
