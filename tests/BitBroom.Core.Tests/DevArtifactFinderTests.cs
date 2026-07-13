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
    public async Task Pycache_needs_sibling_py_source()
    {
        using var sandbox = new TestSandbox();
        // Real Python package: __pycache__ next to source .py → safe (regenerable).
        sandbox.CreateFile(@"pkg\app.py", 500);
        sandbox.CreateFile(@"pkg\__pycache__\app.cpython-312.pyc", 800);
        // .pyc-only distribution (source stripped) → the bytecode is the ONLY code, must NOT be flagged.
        sandbox.CreateFile(@"shipped\__pycache__\secret.cpython-312.pyc", 800);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        DevArtifact hit = Assert.Single(result.Artifacts);
        Assert.Contains(@"pkg\__pycache__", hit.Path);
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

    // -------------------------------------------------------------------------
    // Runtime-app refusal (regression: BitBroom recycled Cursor's out/ + node_modules
    // because installed Electron apps look exactly like dev projects)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Electron_app_layout_is_never_flagged()
    {
        using var sandbox = new TestSandbox();
        // Replica of the real incident: %LOCALAPPDATA%\Programs\cursor — an Electron app
        // whose resources\app is package.json + out + node_modules. icudtl.dat at the app
        // root is the Electron runtime marker.
        sandbox.CreateFile(@"apps\cursor\icudtl.dat", 10);
        sandbox.CreateFile(@"apps\cursor\cursor.exe", 10);
        sandbox.CreateFile(@"apps\cursor\resources\app\package.json", 10);
        sandbox.CreateFile(@"apps\cursor\resources\app\out\main.js", 5000);
        sandbox.CreateFile(@"apps\cursor\resources\app\node_modules\dep\index.js", 5000);
        // A genuine project elsewhere in the same scan proves the scan still works.
        sandbox.CreateFile(@"myproject\package.json", 10);
        sandbox.CreateFile(@"myproject\node_modules\lib.js", 2000);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        DevArtifact only = Assert.Single(result.Artifacts);
        Assert.Contains("myproject", only.Path);
        Assert.DoesNotContain(result.Artifacts, a => a.Path.Contains("cursor"));
    }

    [Fact]
    public async Task Squirrel_and_asar_marked_trees_are_never_flagged()
    {
        using var sandbox = new TestSandbox();
        // Squirrel.Windows layout (Discord/Slack style): Update.exe above the app.
        sandbox.CreateFile(@"slack\Update.exe", 10);
        sandbox.CreateFile(@"slack\app-4.1\web\package.json", 10);
        sandbox.CreateFile(@"slack\app-4.1\web\dist\bundle.js", 3000);
        // Packed Electron: an .asar next to the unpacked project.
        sandbox.CreateFile(@"packed\app.asar", 10);
        sandbox.CreateFile(@"packed\app\package.json", 10);
        sandbox.CreateFile(@"packed\app\node_modules\x.js", 3000);

        var finder = new DevArtifactFinder();
        DevArtifactScanResult result = await finder.ScanAsync(sandbox.Root);

        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task Delete_time_reverify_refuses_runtime_app_locations()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"proj\package.json", 10);
        sandbox.CreateFile(@"proj\node_modules\a.js", 1000);

        var finder = new DevArtifactFinder();
        DevArtifact artifact = Assert.Single((await finder.ScanAsync(sandbox.Root)).Artifacts);

        // The app marker appears AFTER the scan (e.g. the folder is actually an app the
        // scan raced with) — IsArtifact must now refuse it, so the deleter skips it.
        sandbox.CreateFile(@"proj\icudtl.dat", 10);
        Assert.False(DevArtifactFinder.IsArtifact(artifact.Path));

        using var logger = new RunLogger(Path.Combine(sandbox.Root, "logs"), "test");
        var deleter = new DuplicateDeleter(logger);
        DuplicateDeleteResult result = deleter.RecycleDevArtifacts([artifact]);

        Assert.Equal(0, result.Recycled);
        Assert.Equal(1, result.RefusedByGuard);
        Assert.True(Directory.Exists(artifact.Path));
    }

    [Fact]
    public void Runtime_location_checks_cover_appdata_and_profile_dot_folders()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The exact real-world victims from the incident report.
        Assert.True(DevArtifactFinder.IsRuntimeAppLocation(
            Path.Combine(localAppData, "Programs", "cursor", "resources", "app", "out")));
        Assert.True(DevArtifactFinder.IsRuntimeAppLocation(
            Path.Combine(localAppData, "hermes", "hermes-agent", "node_modules")));
        Assert.True(DevArtifactFinder.IsRuntimeAppLocation(
            Path.Combine(profile, ".cursor", "extensions", "some.ext-1.0.0", "dist")));

        // Ordinary project locations stay allowed.
        Assert.False(DevArtifactFinder.IsRuntimeAppLocation(
            Path.Combine(profile, "Desktop", "Work", "myapp", "node_modules")));
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
