using System.Text;

namespace BitBroom.Core.Analyzer;

/// <summary>
/// Builds the CSV export for an analysis (largest files + file-type breakdown).
/// Pure and dependency-free so it is unit-testable independently of the file dialog
/// the GUI uses to choose a destination.
/// </summary>
public static class AnalyzerCsv
{
    public const string Header = "Section,Name,Path,SizeBytes,FileCount";

    public static string Build(IEnumerable<LargeFile> largestFiles, IEnumerable<FileTypeStat> fileTypes)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");

        foreach (LargeFile file in largestFiles)
        {
            sb.Append("LargestFile,")
              .Append(Quote(Path.GetFileName(file.Path))).Append(',')
              .Append(Quote(file.Path)).Append(',')
              .Append(file.SizeBytes).Append(',')
              .Append('1').Append("\r\n");
        }

        foreach (FileTypeStat type in fileTypes)
        {
            sb.Append("FileType,")
              .Append(Quote(type.Extension)).Append(',')
              .Append(',')
              .Append(type.TotalBytes).Append(',')
              .Append(type.FileCount).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>RFC-4180 quoting: wrap in quotes and double any embedded quotes.</summary>
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
