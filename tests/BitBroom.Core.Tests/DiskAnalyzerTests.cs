using BitBroom.Core.Analyzer;
using Xunit;

namespace BitBroom.Core.Tests;

public class DiskAnalyzerTests
{
    [Fact]
    public async Task Computes_sizes_and_sorts_children()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"tree\small\a.bin", 100);
        sandbox.CreateFile(@"tree\big\b.bin", 10_000);
        sandbox.CreateFile(@"tree\big\c.bin", 20_000);
        sandbox.CreateFile(@"tree\rootfile.bin", 5);

        var analyzer = new DiskAnalyzer();
        AnalyzerResult result = await analyzer.AnalyzeAsync(Path.Combine(sandbox.Root, "tree"));

        Assert.Equal(30_105, result.TotalBytes);
        Assert.Equal(4, result.TotalFiles);
        Assert.Equal("big", result.Root.Children[0].Name);
        Assert.Equal(30_000, result.Root.Children[0].SizeBytes);
        Assert.Equal(2, result.Root.Children[0].FileCount);
    }

    [Fact]
    public async Task Junctions_are_not_traversed()
    {
        using var sandbox = new TestSandbox();
        sandbox.CreateFile(@"scan\real.bin", 100);
        string outside = sandbox.CreateDirectory("outside");
        sandbox.CreateFile(@"outside\huge.bin", 1_000_000);

        string junction = Path.Combine(sandbox.Root, "scan", "link");
        if (!TestSandbox.TryCreateJunction(junction, outside))
        {
            return;
        }

        var analyzer = new DiskAnalyzer();
        AnalyzerResult result = await analyzer.AnalyzeAsync(Path.Combine(sandbox.Root, "scan"));

        Assert.Equal(100, result.TotalBytes);
        Assert.Equal(1, result.SkippedReparsePoints);
    }
}
