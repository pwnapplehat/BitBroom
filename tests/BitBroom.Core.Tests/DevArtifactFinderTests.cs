using BitBroom.Core.Dupes;
using BitBroom.Core.Logging;
using BitBroom.Core.Settings;
using Xunit;

namespace BitBroom.Core.Tests;

public class DevArtifactFinderTests
{
    [Fact]
    public async Task Finds_node_modules_only_next_to_package_json()
    {
        using var sandbox = new TestSandbox();
        // Real project: node_modules beside package.json → flagged.
        sandbox.CreateFile(@"proj\package.json", 10);
        sandbox.CreateFile(@"proj\node_modules\left-pad\index.js", 2048);
        // A folder literally named node_modules but NOT a project (no package.json) → ignored.
        sandbox.CreateFile(@"notes\node_modules\random.txt", 4096);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        DevArtifact hit = Assert.Single(result.Artifacts);
        Assert.EndsWith("node_modules", hit.Path);
        Assert.Contains("proj", hit.Path);
    }

    [Fact]
    public async Task Rust_target_requires_cargo_toml()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"rustproj\Cargo.toml", 10);
        sandbox.CreateFile(@"rustproj\target\debug\app.exe", 5000);
        sandbox.CreateFile(@"docs\target\notes.txt", 1000); // not a project → ignored

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        DevArtifact hit = Assert.Single(result.Artifacts);
        Assert.Contains("rustproj", hit.Path);
        Assert.Equal("Rust/Maven build output", hit.Kind);
    }

    [Fact]
    public async Task Venv_identified_by_pyvenv_cfg()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"py\.venv\pyvenv.cfg", 100);
        sandbox.CreateFile(@"py\.venv\Lib\site-packages\pkg.py", 3000);
        sandbox.CreateDirectory("notvenv", ".venv"); // no pyvenv.cfg → ignored

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        DevArtifact hit = Assert.Single(result.Artifacts);
        Assert.Contains(@"py\.venv", hit.Path, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Python virtualenv", hit.Kind);
    }

    [Fact]
    public async Task Does_not_recurse_into_a_matched_artifact()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"proj\package.json", 10);
        // A nested project inside node_modules must NOT be reported separately.
        sandbox.CreateFile(@"proj\node_modules\dep\package.json", 10);
        sandbox.CreateFile(@"proj\node_modules\dep\node_modules\sub\index.js", 1000);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        DevArtifact hit = Assert.Single(result.Artifacts);
        Assert.EndsWith(@"proj\node_modules", hit.Path);
    }

    [Fact]
    public async Task Reports_size_and_total()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"proj\package.json", 10);
        sandbox.CreateFile(@"proj\node_modules\a.js", 1000);
        sandbox.CreateFile(@"proj\node_modules\b.js", 2000);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        Assert.Equal(3000, result.Artifacts[0].SizeBytes);
        Assert.Equal(3000, result.TotalBytes);
    }

    [Fact]
    public async Task Deleter_refuses_when_the_project_manifest_vanished_since_the_scan()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"proj\package.json", 10);
        sandbox.CreateFile(@"proj\node_modules\a.js", 1000);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult scan = await finder.ScanAsync(sandbox.Root);
        DevArtifact artifact = Assert.Single(scan.Artifacts);

        // TOCTOU: project manifest removed between scan and recycle → folder no longer
        // qualifies as an artifact and must be refused, not recycled.
        File.Delete(Path.Combine(sandbox.Root, @"proj\package.json"));

        using var logger = new RunLogger(Path.Combine(sandbox.Root, "logs"), "test");
        var deleter = new DuplicateDeleter(logger);
        DuplicateDeleteResult result = deleter.RecycleDevArtifacts([artifact]);

        Assert.Equal(0, result.Recycled);
        Assert.Equal(1, result.RefusedByGuard);
        Assert.True(Directory.Exists(artifact.Path));
    }

    [SkippableRecycleFact]
    public async Task Deleter_recycles_a_dev_artifact_end_to_end()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"proj\package.json", 10);
        sandbox.CreateFile(@"proj\node_modules\dep\index.js", 2048);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult scan = await finder.ScanAsync(sandbox.Root);
        DevArtifact artifact = Assert.Single(scan.Artifacts);

        using var logger = new RunLogger(Path.Combine(sandbox.Root, "logs"), "test");
        var deleter = new DuplicateDeleter(logger);
        DuplicateDeleteResult result = deleter.RecycleDevArtifacts([artifact]);

        Assert.Equal(1, result.Recycled);
        Assert.Equal(0, result.Failed);
        Assert.False(Directory.Exists(artifact.Path));
        // The project itself is untouched.
        Assert.True(File.Exists(Path.Combine(sandbox.Root, @"proj\package.json")));
    }
}
