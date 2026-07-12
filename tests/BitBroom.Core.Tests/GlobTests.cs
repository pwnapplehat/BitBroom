using BitBroom.Core.Util;
using Xunit;

namespace BitBroom.Core.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("file.txt", "*", true)]
    [InlineData("file.txt", "*.txt", true)]
    [InlineData("file.txt", "*.log", false)]
    [InlineData("FILE.TXT", "*.txt", true)]
    [InlineData("thumbcache_1024.db", "thumbcache_*.db", true)]
    [InlineData("iconcache_16.db", "thumbcache_*.db", false)]
    [InlineData("a.tmp", "?.tmp", true)]
    [InlineData("ab.tmp", "?.tmp", false)]
    [InlineData("MPLog-20260101-010101.log", "*.log", true)]
    [InlineData("memory.dmp", "memory.dmp", true)]
    [InlineData("memory.dmp.bak", "memory.dmp", false)]
    [InlineData("CbsPersist_20260101.cab", "*.persist*", false)]
    [InlineData("weird[name].txt", "weird[name].txt", true)]
    [InlineData("a+b.txt", "a+b.txt", true)]
    public void Matches_expected(string text, string pattern, bool expected)
        => Assert.Equal(expected, Glob.IsMatch(text, pattern));

    [Fact]
    public void MatchAny_works()
    {
        Assert.True(Glob.IsMatchAny("x.log", ["*.txt", "*.log"]));
        Assert.False(Glob.IsMatchAny("x.db", ["*.txt", "*.log"]));
    }

    [Fact]
    public void HasWildcards_detects()
    {
        Assert.True(Glob.HasWildcards("Profile *"));
        Assert.True(Glob.HasWildcards("a?c"));
        Assert.False(Glob.HasWildcards("Default"));
    }
}
