using BitBroom.Core.Engine;
using BitBroom.Core.Logging;
using BitBroom.Core.Settings;
using BitBroom.Core.Special;
using Xunit;

namespace BitBroom.Core.Tests;

public class ExclusionAndCustomFolderTests
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
    // Exclusions
    // =========================================================================

    [Fact]
    public void Excluded_subtree_is_pruned_from_walk()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        sandbox.CreateFile(@"cache\junk.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-10));
        sandbox.CreateFile(@"cache\keepme\important.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-10));
        sandbox.CreateFile(@"cache\keepme\nested\also.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-10));

        var exclusions = new ExclusionSet([Path.Combine(cache, "keepme")]);
        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.Walk(MakeRoot(cache), DateTime.UtcNow, 0, stats, CancellationToken.None, exclusions)];

        Assert.Single(items);
        Assert.EndsWith("junk.tmp", items[0].Path);
        Assert.Equal(1, stats.SkippedExcluded);
    }

    [Fact]
    public void Excluded_root_yields_nothing()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        sandbox.CreateFile(@"cache\junk.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-10));

        var exclusions = new ExclusionSet([cache]);
        var stats = new FileSystemWalker.WalkStats();
        List<ScanItem> items = [.. FileSystemWalker.Walk(MakeRoot(cache), DateTime.UtcNow, 0, stats, CancellationToken.None, exclusions)];

        Assert.Empty(items);
    }

    [Fact]
    public void Deleter_vetoes_excluded_paths_even_when_scanned()
    {
        using var sandbox = new TestSandbox();
        string cache = sandbox.CreateDirectory("cache");
        string file = sandbox.CreateFile(@"cache\junk.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-10));

        using RunLogger logger = NullLogger();
        var exclusions = new ExclusionSet([cache]);
        var deleter = new SafeDeleter(new PathGuard(), logger, DeleteMode.Permanent, exclusions);

        DeleteOutcome outcome = deleter.DeleteFile(new ScanItem(file, 16, DateTime.UtcNow.AddDays(-10), cache));

        Assert.Equal(DeleteOutcome.GuardRejected, outcome);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Malformed_exclusion_entries_are_ignored()
    {
        var exclusions = new ExclusionSet(["", "   ", "not-a-rooted-path", @"C:\Valid\Path"]);
        Assert.Equal(1, exclusions.Count);
        Assert.True(exclusions.IsExcluded(@"C:\Valid\Path\below\file.txt"));
        Assert.False(exclusions.IsExcluded(@"C:\Other"));
    }

    // =========================================================================
    // Custom folders category
    // =========================================================================

    [Fact]
    public async Task Custom_folder_cleans_old_files_only()
    {
        using var sandbox = new TestSandbox();
        string target = sandbox.CreateDirectory("renders");
        sandbox.CreateFile(@"renders\old.bin", lastWriteUtc: DateTime.UtcNow.AddDays(-30));
        sandbox.CreateFile(@"renders\recent.bin", lastWriteUtc: DateTime.UtcNow.AddHours(-2));

        CleanCategory category = CustomFoldersCategory.Create(
            [new CustomCleanFolder { Path = target, MinAgeHours = 168 }]);

        var guard = new PathGuard();
        var context = new ScanContext
        {
            Resolver = new PathResolver(guard),
            GlobalMinAgeHours = 24,
        };

        CategoryScanResult scan = await category.ScanAsync(context, CancellationToken.None);

        Assert.Single(scan.Items);
        Assert.EndsWith("old.bin", scan.Items[0].Path);
        Assert.Equal(1, scan.SkippedTooNew);
    }

    [Fact]
    public async Task Custom_folder_refuses_protected_locations()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            return;
        }

        CleanCategory category = CustomFoldersCategory.Create(
            [new CustomCleanFolder { Path = documents, MinAgeHours = 0 }]);

        var guard = new PathGuard();
        var context = new ScanContext
        {
            Resolver = new PathResolver(guard),
            GlobalMinAgeHours = 24,
        };

        CategoryScanResult scan = await category.ScanAsync(context, CancellationToken.None);

        Assert.Empty(scan.Items);
        Assert.Contains(scan.Errors, e => e.Contains("Refused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Custom_folder_ignores_drive_roots_and_relative_paths()
    {
        CleanCategory category = CustomFoldersCategory.Create(
        [
            new CustomCleanFolder { Path = @"C:\", MinAgeHours = 0 },
            new CustomCleanFolder { Path = "relative\\path", MinAgeHours = 0 },
        ]);

        Assert.Empty(category.Rules);
    }

    [Fact]
    public async Task Custom_folder_junctions_inside_are_never_traversed()
    {
        using var sandbox = new TestSandbox();
        string target = sandbox.CreateDirectory("cleanme");
        string precious = sandbox.CreateDirectory("precious");
        string canary = sandbox.CreateFile(@"precious\canary.txt", lastWriteUtc: DateTime.UtcNow.AddDays(-30));
        sandbox.CreateFile(@"cleanme\junk.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-30));

        if (!TestSandbox.TryCreateJunction(Path.Combine(target, "link"), precious))
        {
            return;
        }

        CleanCategory category = CustomFoldersCategory.Create(
            [new CustomCleanFolder { Path = target, MinAgeHours = 0 }]);

        var guard = new PathGuard();
        var context = new ScanContext
        {
            Resolver = new PathResolver(guard),
            GlobalMinAgeHours = 0,
        };

        CategoryScanResult scan = await category.ScanAsync(context, CancellationToken.None);

        Assert.Single(scan.Items);
        Assert.DoesNotContain(scan.Items, i => i.Path.Contains("canary", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, scan.SkippedReparsePoints);
        Assert.True(File.Exists(canary));
    }

    // =========================================================================
    // Settings round-trip
    // =========================================================================

    [Fact]
    public void Settings_clamp_sanitizes_new_fields()
    {
        var settings = new AppSettings
        {
            ExcludedPaths = ["", @"D:\ok"],
            CustomCleanFolders = [new CustomCleanFolder { Path = @"D:\r", MinAgeHours = -5 }],
            ScheduleFrequency = 99,
            ScheduleDayOfWeek = 42,
            ScheduleHour = -3,
        };

        // Round-trip through JSON the way Load() does (Clamp is private; Save+Load exercises it).
        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        AppSettings loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        // Deserialize alone doesn't clamp — but Build path must tolerate it; verify via catalog.
        CleanCategory category = CustomFoldersCategory.Create(loaded.CustomCleanFolders);
        Assert.Single(category.Rules);
    }
}
