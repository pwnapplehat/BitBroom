using System.Globalization;

namespace BitBroom.Core.Util;

/// <summary>
/// Parses human disk sizes for the CLI (<c>20G</c>, <c>1.5GB</c>, <c>512MB</c>) using the
/// same binary units as <see cref="ByteFormatter"/> (1024). A bare integer is accepted
/// only when it is already a byte count of at least 1 MiB, so <c>20</c> cannot silently
/// mean twenty bytes.
/// </summary>
public static class ByteSizeParser
{
    /// <summary>1 MiB — the minimum bare integer accepted as a raw byte count.</summary>
    public const long MinimumBareByteCount = 1024L * 1024L;

    public static bool TryParse(string? text, out long bytes, out string error)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Size value is empty.";
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed[0] == '-')
        {
            error = "Size must be positive.";
            return false;
        }

        int suffixAt = 0;
        while (suffixAt < trimmed.Length)
        {
            char c = trimmed[suffixAt];
            if (char.IsAsciiDigit(c) || c == '.' || c == '+')
            {
                suffixAt++;
                continue;
            }

            break;
        }

        if (suffixAt == 0)
        {
            error = $"Invalid size '{text}'.";
            return false;
        }

        string numberPart = trimmed[..suffixAt].Trim();
        string suffix = trimmed[suffixAt..].Trim();

        if (!double.TryParse(numberPart, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double value)
            || value <= 0d || double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"Invalid size '{text}'.";
            return false;
        }

        long multiplier;
        if (suffix.Length == 0)
        {
            if (numberPart.Contains('.'))
            {
                error = $"Value '{text}' is missing a unit; use e.g. 20G or 20GB.";
                return false;
            }

            if (value < MinimumBareByteCount)
            {
                error = $"Value '{text}' is missing a unit; use e.g. {trimmed}G or {trimmed}GB, or pass a byte count of at least 1 MB.";
                return false;
            }

            multiplier = 1;
        }
        else
        {
            multiplier = suffix.ToLowerInvariant() switch
            {
                "b" => 1,
                "k" or "kb" or "kib" => 1024L,
                "m" or "mb" or "mib" => 1024L * 1024L,
                "g" or "gb" or "gib" => 1024L * 1024L * 1024L,
                "t" or "tb" or "tib" => 1024L * 1024L * 1024L * 1024L,
                _ => -1L,
            };

            if (multiplier < 0)
            {
                error = $"Unknown size unit '{suffix}'. Use B, K/KB, M/MB, G/GB, or T/TB.";
                return false;
            }
        }

        double raw = value * multiplier;
        if (raw < 1d || raw > long.MaxValue)
        {
            error = $"Size '{text}' is out of range.";
            return false;
        }

        bytes = (long)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (bytes <= 0)
        {
            error = "Size must be greater than zero.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
