using BitBroom.Core.Engine;
using BitBroom.Core.Util;
using Xunit;

namespace BitBroom.Core.Tests;

public class ByteSizeParserTests
{
    [Theory]
    [InlineData("20G", 20L * 1024 * 1024 * 1024)]
    [InlineData("20GB", 20L * 1024 * 1024 * 1024)]
    [InlineData("20GiB", 20L * 1024 * 1024 * 1024)]
    [InlineData("1.5GB", 1610612736)]
    [InlineData("512MB", 512L * 1024 * 1024)]
    [InlineData("512M", 512L * 1024 * 1024)]
    [InlineData("1K", 1024)]
    [InlineData("1B", 1)]
    [InlineData("1048576", 1048576)]
    [InlineData(" 10g ", 10L * 1024 * 1024 * 1024)]
    public void Parses(string text, long expected)
    {
        Assert.True(ByteSizeParser.TryParse(text, out long bytes, out string error), error);
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("0")]
    [InlineData("-5G")]
    [InlineData("")]
    [InlineData("10X")]
    [InlineData("1.5")]
    [InlineData("gb")]
    public void Rejects(string text)
        => Assert.False(ByteSizeParser.TryParse(text, out _, out _));
}

public class FreeSpaceGoalTests
{
    [Fact]
    public void Drive_letter_forms_normalize_to_root()
    {
        Assert.True(FreeSpaceGoal.TryParseDrive("c", out string a, out _));
        Assert.True(FreeSpaceGoal.TryParseDrive("C:", out string b, out _));
        Assert.True(FreeSpaceGoal.TryParseDrive(@"C:\", out string c, out _));
        Assert.Equal(a, b);
        Assert.Equal(b, c);
        Assert.EndsWith(@"\", a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData(@"\\server\share")]
    public void Rejects_non_drive(string input)
        => Assert.False(FreeSpaceGoal.TryParseDrive(input, out _, out _));

    [Fact]
    public void File_on_another_volume_is_skipped()
    {
        var goal = new FreeSpaceGoal(@"C:\", targetFreeBytes: 10_000, initialAvailableBytes: 0);
        Assert.True(goal.IsOnDrive(@"C:\Users\me\AppData\Local\Temp\x.tmp"));
        Assert.False(goal.IsOnDrive(@"D:\scratch\x.tmp"));
        Assert.True(goal.ShouldSkipItem(new ScanItem(@"D:\scratch\x.tmp", 100, DateTime.UtcNow, @"D:\scratch")));
        Assert.False(goal.ShouldSkipItem(new ScanItem(@"C:\Users\me\AppData\Local\Temp\x.tmp", 100, DateTime.UtcNow, @"C:\Users\me\AppData\Local\Temp")));
    }

    [Fact]
    public void Atomic_category_larger_than_deficit_is_skipped()
    {
        var goal = new FreeSpaceGoal(@"C:\", targetFreeBytes: 1000, initialAvailableBytes: 800);
        Assert.Equal(200, goal.RemainingBytes);
        Assert.True(goal.ShouldSkipAtomic(500, @"C:\Windows.old"));
        Assert.False(goal.ShouldSkipAtomic(200, @"C:\Windows.old"));
        Assert.True(goal.ShouldSkipAtomic(100, @"D:\Windows.old"));
        Assert.False(goal.ShouldSkipAtomic(200, representativePath: null));
    }

    [Fact]
    public void Accounting_reaches_the_target()
    {
        var goal = new FreeSpaceGoal(@"C:\", targetFreeBytes: 1000, initialAvailableBytes: 700);
        Assert.False(goal.IsSatisfied);
        goal.NoteFreed(300);
        Assert.True(goal.IsSatisfied);
        Assert.Equal(0, goal.RemainingBytes);
    }
}
