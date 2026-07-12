using BitBroom.Core.Engine;
using BitBroom.Core.Logging;
using Xunit;

namespace BitBroom.Core.Tests;

public class WalkerAndDeleterTests
{
    private static ResolvedRoot MakeRoot(string path, CleanRule? rule = null)
        => new(PathGuard.Normalize(path), Path.GetDirectoryName(path)!, rule ?? new CleanRule
        {
            Base = KnownBase.Custom,
            RelativePattern = "x",
            CustomBaseProvider = () => null,
        });

    private static RunLogger NullLogger() => new(Path.Combine(Path.GetTempPath(), "BitBroomTests", "logs"), "test");

    // =========================================================================
    // THE canary test: a junction inside a cleaned folder pointing at data
    // outside of it. The data behind the junction must survive a clean.
    // =========================================================================
    [Fact]
    public void Junction_targets_are_never_touched()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string precious = sandbox.CreateDirectory("precious");
        string canary = sandbox.CreateFile(@"precious\canary.txt", lastWriteUtc: DateTime.UtcNow.AddDays(-30));
        sandbox.CreateFile(@"cache\junk1.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-30));
        sandbox.CreateFile(@"cache\sub\junk2.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-30));

        string junction = Path.Combine(cache, "link");
        if (!TestSandbox.TryCreateJunction(junction, precious))
        {
            return; // Junction creation unavailable in this environment; nothing to verify.
        }

        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.Walk(MakeRoot(cache), DateTime.UtcNow, 0, stats, CancellationToken.None)];

        // The walker must have found exactly the two junk files, never the canary.
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Path.Contains("canary", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, stats.SkippedReparsePoints);

        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: false);
        foreach (ScanItem item in items)
        {
            Assert.Equal(DeleteOutcome.Deleted, deleter.DeleteFile(item));
        }

        // Canary intact, junk gone, junction itself untouched.
        Assert.True(File.Exists(canary), "Canary file behind the junction was deleted!");
        Assert.False(File.Exists(Path.Combine(cache, "junk1.tmp")));
        Assert.True(Directory.Exists(junction));
    }

    [Fact]
    public void Age_filter_keeps_fresh_files()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        sandbox.CreateFile(@"cache\old.tmp", lastWriteUtc: DateTime.UtcNow.AddHours(-50));
        sandbox.CreateFile(@"cache\fresh.tmp", lastWriteUtc: DateTime.UtcNow.AddHours(-1));

        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.Walk(MakeRoot(cache), DateTime.UtcNow, 24, stats, CancellationToken.None)];

        Assert.Single(items);
        Assert.EndsWith("old.tmp", items[0].Path);
        Assert.Equal(1, stats.SkippedTooNew);
    }

    [Fact]
    public void FixedFile_rule_respects_age_filter()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateDirectory("home");
        sandbox.CreateFile(@"home\old.log", 100, DateTime.UtcNow.AddDays(-3));
        string fresh = sandbox.CreateFile(@"home\fresh.log", 100, DateTime.UtcNow.AddHours(-1));

        var rule = new CleanRule
        {
            Base = KnownBase.Custom,
            CustomBaseProvider = () => Path.Combine(sandbox.Root, "home"),
            RelativePattern = string.Empty,
            Kind = RuleKind.FixedFiles,
            FilePatterns = ["old.log", "fresh.log"],
        }.Validate();

        var resolver = new PathResolver(new PathGuard());
        List<ResolvedRoot> roots = resolver.ExpandRoots(rule);
        Assert.Single(roots);

        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.ResolveFixedFiles(roots[0], DateTime.UtcNow, 24, stats)];

        Assert.Single(items);
        Assert.EndsWith("old.log", items[0].Path);
        Assert.Equal(1, stats.SkippedTooNew);
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Age_filter_uses_newest_of_creation_and_write_time()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        // Old mtime but fresh creation (like a freshly extracted archive member).
        string path = sandbox.CreateFile(@"cache\extracted.bin");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        // CreationTime stays "now".

        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.Walk(MakeRoot(cache), DateTime.UtcNow, 24, stats, CancellationToken.None)];

        Assert.Empty(items);
        Assert.Equal(1, stats.SkippedTooNew);
    }

    [Fact]
    public void File_patterns_filter()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        sandbox.CreateFile(@"cache\a.log", lastWriteUtc: DateTime.UtcNow.AddDays(-2));
        sandbox.CreateFile(@"cache\b.db", lastWriteUtc: DateTime.UtcNow.AddDays(-2));

        var rule = new CleanRule
        {
            Base = KnownBase.Custom,
            RelativePattern = "cache",
            CustomBaseProvider = () => null,
            FilePatterns = ["*.log"],
            MinAgeHoursOverride = 0,
        };

        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.Walk(MakeRoot(cache, rule), DateTime.UtcNow, 0, stats, CancellationToken.None)];

        Assert.Single(items);
        Assert.EndsWith("a.log", items[0].Path);
    }

    [Fact]
    public void Locked_files_are_skipped_not_errors()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string lockedPath = sandbox.CreateFile(@"cache\locked.bin", lastWriteUtc: DateTime.UtcNow.AddDays(-2));
        string freePath = sandbox.CreateFile(@"cache\free.bin", lastWriteUtc: DateTime.UtcNow.AddDays(-2));

        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: false);

        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(DeleteOutcome.Locked, deleter.DeleteFile(new ScanItem(lockedPath, 16, DateTime.UtcNow, cache)));
            Assert.Equal(DeleteOutcome.Deleted, deleter.DeleteFile(new ScanItem(freePath, 16, DateTime.UtcNow, cache)));
        }

        Assert.True(File.Exists(lockedPath));
        Assert.False(File.Exists(freePath));
    }

    [Fact]
    public void ReadOnly_files_are_deleted()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string path = sandbox.CreateFile(@"cache\readonly.bin", lastWriteUtc: DateTime.UtcNow.AddDays(-2));
        File.SetAttributes(path, FileAttributes.ReadOnly);

        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: false);
        Assert.Equal(DeleteOutcome.Deleted, deleter.DeleteFile(new ScanItem(path, 16, DateTime.UtcNow, cache)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Simulation_deletes_nothing()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string path = sandbox.CreateFile(@"cache\file.bin", lastWriteUtc: DateTime.UtcNow.AddDays(-2));

        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: true);
        Assert.Equal(DeleteOutcome.Simulated, deleter.DeleteFile(new ScanItem(path, 16, DateTime.UtcNow, cache)));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Missing_files_reported_as_missing()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: false);
        Assert.Equal(DeleteOutcome.Missing, deleter.DeleteFile(new ScanItem(Path.Combine(cache, "ghost.bin"), 16, DateTime.UtcNow, cache)));
    }

    [Fact]
    public void Guard_rejects_delete_outside_root_even_if_scanned()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string outside = sandbox.CreateFile("outside.bin");

        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: false);
        Assert.Equal(DeleteOutcome.GuardRejected, deleter.DeleteFile(new ScanItem(outside, 16, DateTime.UtcNow, cache)));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Empty_directories_removed_but_root_kept()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string sub = sandbox.CreateDirectory("cache", "a", "b");
        Assert.True(Directory.Exists(sub));

        using RunLogger logger = NullLogger();
        var deleter = new SafeDeleter(new PathGuard(), logger, simulate: false);
        int removed = deleter.RemoveEmptyDirectories(
            [Path.Combine(cache, "a"), Path.Combine(cache, "a", "b")], cache);

        Assert.Equal(2, removed);
        Assert.True(Directory.Exists(cache));
        Assert.False(Directory.Exists(Path.Combine(cache, "a")));
    }

    [Fact]
    public async Task Category_end_to_end_clean_in_sandbox()
    {
        using var sandbox = new TestSandbox();
        string root = sandbox.CreateDirectory("apphome", "cache");
        sandbox.CreateFile(@"apphome\cache\1.bin", 100, DateTime.UtcNow.AddDays(-3));
        sandbox.CreateFile(@"apphome\cache\deep\2.bin", 200, DateTime.UtcNow.AddDays(-3));
        sandbox.CreateFile(@"apphome\keep.txt", 50, DateTime.UtcNow.AddDays(-3));

        var category = new CleanCategory
        {
            Id = "test-cache",
            Name = "Test cache",
            Description = "sandbox",
            Group = CategoryGroup.Applications,
            Rules =
            [
                new CleanRule
                {
                    Base = KnownBase.Custom,
                    CustomBaseProvider = () => Path.Combine(sandbox.Root, "apphome"),
                    RelativePattern = "cache",
                    MinAgeHoursOverride = 0,
                }.Validate(),
            ],
        };

        var engine = new CleaningEngine();
        var settings = new BitBroom.Core.Settings.AppSettings { MinAgeHours = 24 };

        Dictionary<string, CategoryScanResult> scans = await engine.ScanAsync([category], settings);
        Assert.Equal(300, scans["test-cache"].TotalBytes);
        Assert.Equal(2, scans["test-cache"].Items.Count);

        using RunLogger logger = NullLogger();
        Dictionary<string, CategoryCleanResult> cleans = await engine.CleanAsync([category], scans, settings, logger);

        Assert.Equal(300, cleans["test-cache"].BytesFreed);
        Assert.Equal(2, cleans["test-cache"].Deleted);
        Assert.True(File.Exists(Path.Combine(sandbox.Root, "apphome", "keep.txt")));
        Assert.True(Directory.Exists(root), "Rule root must be preserved");
        Assert.False(Directory.Exists(Path.Combine(root, "deep")), "Emptied subdirectory should be removed");
    }
}
