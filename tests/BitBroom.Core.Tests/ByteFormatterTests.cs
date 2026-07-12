using BitBroom.Core.Util;
using Xunit;

namespace BitBroom.Core.Tests;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1610612736, "1.5 GB")]
    [InlineData(-5, "—")]
    public void Formats(long bytes, string expected)
        => Assert.Equal(expected, ByteFormatter.Format(bytes));
}
