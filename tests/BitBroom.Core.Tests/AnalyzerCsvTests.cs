using BitBroom.Core.Analyzer;
using Xunit;

namespace BitBroom.Core.Tests;

public class AnalyzerCsvTests
{
    [Fact]
    public void Build_emits_header_largest_files_and_types()
    {
        var largest = new List<LargeFile>
        {
            new(@"C:\Users\me\video.mp4", 1_000_000, DateTime.UtcNow),
            new(@"D:\data\dump.bin", 500_000, DateTime.UtcNow),
        };
        var types = new List<FileTypeStat>
        {
            new(".mp4", 1_000_000, 1),
            new(".bin", 500_000, 1),
        };

        string csv = AnalyzerCsv.Build(largest, types);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(AnalyzerCsv.Header, lines[0]);
        Assert.Contains(lines, l => l == "LargestFile,\"video.mp4\",\"C:\\Users\\me\\video.mp4\",1000000,1");
        Assert.Contains(lines, l => l == "FileType,\".mp4\",,1000000,1");
        Assert.Equal(5, lines.Length); // header + 2 files + 2 types
    }

    [Fact]
    public void Build_quotes_fields_with_commas_and_quotes()
    {
        var largest = new List<LargeFile>
        {
            new(@"C:\a,b\weird ""name"".txt", 10, DateTime.UtcNow),
        };

        string csv = AnalyzerCsv.Build(largest, []);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Comma inside the path must stay inside one quoted field; embedded quotes doubled.
        Assert.Contains(lines, l => l.Contains("\"weird \"\"name\"\".txt\""));
        Assert.Contains(lines, l => l.Contains("\"C:\\a,b\\weird \"\"name\"\".txt\""));
    }

    [Fact]
    public void Build_handles_empty_analysis()
    {
        string csv = AnalyzerCsv.Build([], []);
        Assert.Equal(AnalyzerCsv.Header + "\r\n", csv);
    }
}
