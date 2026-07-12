using BitBroom.Core.Engine;
using Xunit;

namespace BitBroom.Core.Tests;

public class PathResolverTests
{
    [Fact]
    public void Expands_wildcard_segments_by_enumeration()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateDirectory("base", "Profile 1", "Cache");
        sandbox.CreateDirectory("base", "Profile 2", "Cache");
        sandbox.CreateDirectory("base", "Default", "Cache");
        sandbox.CreateDirectory("base", "Profile 1", "NotCache");

        var guard = new PathGuard();
        var resolver = new PathResolver(guard);
        var rule = new CleanRule
        {
            Base = KnownBase.Custom,
            CustomBaseProvider = () => Path.Combine(sandbox.Root, "base"),
            RelativePattern = @"Profile *\Cache",
        }.Validate();

        List<ResolvedRoot> roots = resolver.ExpandRoots(rule);
        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.EndsWith("Cache", r.Path));
    }

    [Fact]
    public void Wildcard_expansion_skips_junction_directories()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateDirectory("base", "RealApp", "TempState");
        string outside = sandbox.CreateDirectory("outside");
        sandbox.CreateDirectory("outside", "TempState");
        sandbox.CreateFile(@"outside\TempState\treasure.txt", lastWriteUtc: DateTime.UtcNow.AddDays(-9));

        string junction = Path.Combine(sandbox.Root, "base", "EvilLink");
        if (!TestSandbox.TryCreateJunction(junction, outside))
        {
            return;
        }

        var resolver = new PathResolver(new PathGuard());
        var rule = new CleanRule
        {
            Base = KnownBase.Custom,
            CustomBaseProvider = () => Path.Combine(sandbox.Root, "base"),
            RelativePattern = @"*\TempState",
        }.Validate();

        List<ResolvedRoot> roots = resolver.ExpandRoots(rule);
        Assert.Single(roots);
        Assert.Contains("RealApp", roots[0].Path);
    }

    [Fact]
    public void Missing_base_produces_no_roots()
    {
        var resolver = new PathResolver(new PathGuard());
        var rule = new CleanRule
        {
            Base = KnownBase.Custom,
            CustomBaseProvider = () => null,
            RelativePattern = "whatever",
        }.Validate();

        Assert.Empty(resolver.ExpandRoots(rule));
    }

    [Fact]
    public void Rule_validation_rejects_traversal_and_rooted_patterns()
    {
        Assert.Throws<InvalidOperationException>(() => new CleanRule
        {
            Base = KnownBase.LocalAppData,
            RelativePattern = @"Temp\..\..\Documents",
        }.Validate());

        Assert.Throws<InvalidOperationException>(() => new CleanRule
        {
            Base = KnownBase.LocalAppData,
            RelativePattern = @"C:\Windows",
        }.Validate());
    }

    [Fact]
    public void Base_overrides_are_honored()
    {
        using var sandbox = new TestSandbox();
        string fakeLocal = sandbox.CreateDirectory("FakeLocal");
        sandbox.CreateDirectory("FakeLocal", "Temp");

        var resolver = new PathResolver(new PathGuard(), new Dictionary<KnownBase, string?>
        {
            [KnownBase.LocalAppData] = fakeLocal,
        });

        var rule = new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = "Temp" }.Validate();
        List<ResolvedRoot> roots = resolver.ExpandRoots(rule);

        Assert.Single(roots);
        Assert.Equal(PathGuard.Normalize(Path.Combine(fakeLocal, "Temp")), roots[0].Path);
    }
}
