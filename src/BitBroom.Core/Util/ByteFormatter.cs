using System.Globalization;

namespace BitBroom.Core.Util;

/// <summary>Human-readable byte formatting (binary units, matching Explorer conventions).</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < Units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        string number = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture)
            : value >= 10 ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

        return $"{number} {Units[unit]}";
    }

    public static string Format(long? bytes) => bytes.HasValue ? Format(bytes.Value) : "—";
}
