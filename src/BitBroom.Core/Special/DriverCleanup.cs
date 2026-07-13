using System.Text.RegularExpressions;

namespace BitBroom.Core.Special;

/// <summary>A third-party driver package staged in the Windows DriverStore.</summary>
public sealed record StagedDriver(
    string PublishedName,   // oemNN.inf
    string OriginalName,    // e.g. nv_dispi.inf
    string Provider,
    string ClassName,
    Version Version,
    DateTime Date);

/// <summary>A set of superseded driver versions safe to remove (newest of each family kept).</summary>
public sealed record SupersededDriver(StagedDriver Driver, StagedDriver KeptInstead);

/// <summary>
/// Safe removal of *superseded* driver packages from the DriverStore — the 5–40 GB of old
/// NVIDIA/Intel/Realtek versions Windows keeps after every driver update. Uses the official
/// <c>pnputil</c> tool only. Safety is structural: drivers are grouped by (OriginalName,
/// Provider, Class); within each family the newest version (by version then date) is ALWAYS
/// kept, and only strictly-older duplicates are offered for removal. Families with a single
/// version are never touched, so a unique/in-use driver can't be removed. Requires admin.
/// This is the same policy Disk Cleanup's "Device driver packages" handler applies.
/// </summary>
public static class DriverCleanup
{
    public static async Task<List<StagedDriver>> EnumerateAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync("pnputil.exe", "/enum-drivers", TimeSpan.FromMinutes(2), null, ct)
            .ConfigureAwait(false);

        var drivers = ParseEnumOutput(result.Output);
        onLine?.Invoke($"Found {drivers.Count} third-party driver package(s) in the DriverStore.");
        return drivers;
    }

    /// <summary>
    /// Groups by driver family and returns the strictly-older versions (newest kept). The
    /// result is deterministic and never includes the version being kept. Same-version
    /// duplicates are deliberately NOT offered: multiple staged copies of one version can
    /// legitimately serve different hardware IDs, so only a strictly-newer version proves
    /// a package is superseded.
    /// </summary>
    public static List<SupersededDriver> FindSuperseded(IReadOnlyList<StagedDriver> drivers)
    {
        var superseded = new List<SupersededDriver>();

        IEnumerable<IGrouping<string, StagedDriver>> families = drivers.GroupBy(
            d => $"{d.OriginalName}|{d.Provider}|{d.ClassName}".ToLowerInvariant());

        foreach (IGrouping<string, StagedDriver> family in families)
        {
            List<StagedDriver> ordered = [.. family
                .OrderByDescending(d => d.Version)
                .ThenByDescending(d => d.Date)];

            if (ordered.Count < 2)
            {
                continue; // Unique driver — never a removal candidate.
            }

            StagedDriver keep = ordered[0];
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Version < keep.Version)
                {
                    superseded.Add(new SupersededDriver(ordered[i], keep));
                }
            }
        }

        return superseded;
    }

    /// <summary>
    /// Deletes one superseded driver via 'pnputil /delete-driver oemNN.inf'. Deliberately
    /// WITHOUT /uninstall or /force: pnputil refuses to remove a package still bound to a
    /// live device, so an in-use driver is protected by the tool itself.
    /// </summary>
    public static async Task<(bool Removed, string Message)> DeleteAsync(StagedDriver driver, Action<string>? onLine = null, CancellationToken ct = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "pnputil.exe", $"/delete-driver {driver.PublishedName}", TimeSpan.FromMinutes(2), onLine, ct)
            .ConfigureAwait(false);

        // pnputil returns non-zero when the package is still in use — that's a safe refusal.
        bool removed = result.Success;
        string message = removed
            ? $"Removed {driver.PublishedName} ({driver.Provider} {driver.OriginalName} {driver.Version})"
            : $"Kept {driver.PublishedName} — pnputil refused (likely still in use): {FirstLine(result.Error, result.Output)}";
        return (removed, message);
    }

    // -------------------------------------------------------------------------
    // Parsing (locale-tolerant: match on the value layout, not localized labels)
    // -------------------------------------------------------------------------

    public static List<StagedDriver> ParseEnumOutput(string output)
    {
        var drivers = new List<StagedDriver>();

        // pnputil prints a block per driver; labels are localized but the values are stable.
        // We split into blocks on the "Published Name:" anchor (oemNN.inf is invariant).
        string[] lines = output.Replace("\r\n", "\n").Split('\n');

        string? published = null, original = null, provider = null, className = null;
        Version version = new(0, 0);
        DateTime date = DateTime.MinValue;

        void Flush()
        {
            if (published is not null)
            {
                drivers.Add(new StagedDriver(
                    published,
                    original ?? published,
                    provider ?? "Unknown",
                    className ?? "Unknown",
                    version,
                    date));
            }

            published = original = provider = className = null;
            version = new Version(0, 0);
            date = DateTime.MinValue;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string value = line[(colon + 1)..].Trim();

            // oemNN.inf as a value marks a new block's Published Name regardless of locale.
            if (Regex.IsMatch(value, @"^oem\d+\.inf$", RegexOptions.IgnoreCase))
            {
                Flush();
                published = value.ToLowerInvariant();
            }
            else if (Regex.IsMatch(value, @"\.inf$", RegexOptions.IgnoreCase))
            {
                original = value.ToLowerInvariant();
            }
            else if (TryParseDriverDateVersion(value, out DateTime d, out Version v))
            {
                date = d;
                version = v;
            }
            else if (provider is null && LooksLikeProvider(line, value))
            {
                provider = value;
            }
            else if (className is null && LooksLikeClass(line, value))
            {
                className = value;
            }
        }

        Flush();
        return drivers;
    }

    // "Driver Version: 09/12/2025 32.0.15.7652" — date then version, locale date order varies.
    private static bool TryParseDriverDateVersion(string value, out DateTime date, out Version version)
    {
        date = DateTime.MinValue;
        version = new Version(0, 0);

        Match m = Regex.Match(value, @"(\d{1,4}[/\-.]\d{1,2}[/\-.]\d{1,4})\s+(\d+(?:\.\d+){1,3})");
        if (!m.Success)
        {
            return false;
        }

        DateTime.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.DateTimeStyles.None, out date);
        Version.TryParse(m.Groups[2].Value, out Version? v);
        version = v ?? new Version(0, 0);
        return true;
    }

    private static bool LooksLikeProvider(string line, string value) =>
        value.Length is > 0 and < 100 &&
        (line.StartsWith("Provider", StringComparison.OrdinalIgnoreCase) || line.Contains("Provider", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeClass(string line, string value) =>
        value.Length is > 0 and < 100 &&
        (line.StartsWith("Class", StringComparison.OrdinalIgnoreCase) || line.Contains("Class Name", StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            string? line = candidate?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (!string.IsNullOrEmpty(line))
            {
                return line;
            }
        }

        return "unknown";
    }
}
