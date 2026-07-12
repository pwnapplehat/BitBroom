using System.Text;
using System.Text.Json;
using BitBroom.Cli;
using BitBroom.Core.Analyzer;
using BitBroom.Core.Catalog;
using BitBroom.Core.Engine;
using BitBroom.Core.Hogs;
using BitBroom.Core.Logging;
using BitBroom.Core.Settings;
using BitBroom.Core.Util;

Console.OutputEncoding = Encoding.UTF8;

var arguments = new ArgumentParser(args);
if (arguments.Error is not null)
{
    Console.Error.WriteLine(arguments.Error);
    return ExitCodes.BadArguments;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
    Console.Error.WriteLine("Cancelling…");
};

try
{
    return arguments.Command switch
    {
        "list" => CommandList(arguments),
        "scan" => await CommandScanAsync(arguments, cancellation.Token),
        "clean" => await CommandCleanAsync(arguments, cancellation.Token),
        "hogs" => await CommandHogsAsync(arguments, cancellation.Token),
        "analyze" => await CommandAnalyzeAsync(arguments, cancellation.Token),
        "version" => CommandVersion(),
        _ => CommandHelp(arguments),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Aborted.");
    return ExitCodes.Aborted;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    return ExitCodes.Fatal;
}

// ===========================================================================
// Commands
// ===========================================================================

static int CommandVersion()
{
    Console.WriteLine($"BitBroom {VersionInfo.Version}");
    return ExitCodes.Ok;
}

static int CommandHelp(ArgumentParser arguments)
{
    if (arguments.Command is not (null or "help" or "--help" or "-h"))
    {
        Console.Error.WriteLine($"Unknown command '{arguments.Command}'.");
    }

    Console.WriteLine(
        """
        BitBroom — safety-first disk cleaner for Windows. https://github.com/pwnapplehat/BitBroom

        USAGE
          bitbroom-cli <command> [options]

        COMMANDS
          list                       List all cleaning categories.
          scan                       Measure reclaimable space (read-only, always safe).
          clean                      Delete what scan found. Requires --yes (or use --dry-run).
          hogs                       Report hidden space hogs (hiberfil, WSL disks, restore points…).
          analyze <path>             Directory size breakdown + largest files.
          version                    Print version.

        CATEGORY SELECTION (scan/clean)
          --defaults                 The safe, on-by-default category set. (default)
          --all                      Every category, including Moderate risk. Advanced ones still excluded.
          --categories, -c a,b,c     Explicit category ids (see 'list').

        OPTIONS
          --dry-run                  Clean in simulation mode: log everything, delete nothing.
          --yes, -y                  Confirm deletion (required for a real clean).
          --min-age <hours>          Override the minimum file age (default from settings, 24h).
          --json                     Machine-readable JSON output.
          --top <n>                  analyze: number of largest files to print (default 25).
          --depth <n>                analyze: tree depth to print (default 2).

        EXIT CODES
          0 ok · 1 fatal · 2 completed with skips/errors · 3 bad arguments · 4 needs admin · 5 aborted
        """);
    return arguments.Command is null or "help" or "--help" or "-h" ? ExitCodes.Ok : ExitCodes.BadArguments;
}

static int CommandList(ArgumentParser arguments)
{
    IReadOnlyList<CleanCategory> categories = CategoryCatalog.Build();

    if (arguments.Json)
    {
        var payload = categories.Select(c => new
        {
            c.Id,
            c.Name,
            Group = c.Group.ToString(),
            Risk = c.Risk.ToString(),
            Default = c.EnabledByDefault,
            Admin = c.RequiresAdmin,
            c.Description,
        });
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return ExitCodes.Ok;
    }

    Console.WriteLine();
    Console.WriteLine($"{"ID",-28} {"GROUP",-13} {"RISK",-9} {"DEFAULT",-8} {"ADMIN",-6} NAME");
    Console.WriteLine(new string('─', 100));
    foreach (CleanCategory category in categories.OrderBy(c => c.Group).ThenBy(c => c.Id, StringComparer.Ordinal))
    {
        Console.WriteLine($"{category.Id,-28} {category.Group,-13} {category.Risk,-9} {(category.EnabledByDefault ? "yes" : "-"),-8} {(category.RequiresAdmin ? "yes" : "-"),-6} {category.Name}");
    }

    Console.WriteLine();
    Console.WriteLine($"{categories.Count} categories. 'bitbroom-cli scan' measures the default set; see docs/CATEGORIES.md for details.");
    return ExitCodes.Ok;
}

static async Task<int> CommandScanAsync(ArgumentParser arguments, CancellationToken ct)
{
    (List<CleanCategory>? selected, int error) = SelectCategories(arguments);
    if (selected is null)
    {
        return error;
    }

    AppSettings settings = AppSettings.Load();
    if (arguments.MinAgeHours is int minAge)
    {
        settings.MinAgeHours = minAge;
    }

    WarnAboutAdminCategories(selected);

    var engine = new CleaningEngine();
    if (!arguments.Json)
    {
        Console.WriteLine($"Scanning {selected.Count} categories (min file age {settings.MinAgeHours}h)…");
    }

    Dictionary<string, CategoryScanResult> scans = await engine.ScanAsync(
        selected,
        settings,
        progress: arguments.Json ? null : new Progress<ScanProgress>(p =>
            Console.Write($"\r  [{p.CategoriesDone}/{p.CategoriesTotal}] {ByteFormatter.Format(p.BytesFoundSoFar)} found — {Truncate(p.CategoryName, 44),-44}")),
        cancellationToken: ct);

    if (!arguments.Json)
    {
        Console.WriteLine();
        Console.WriteLine();
    }

    PrintScanResults(selected, scans, arguments.Json);
    return ExitCodes.Ok;
}

/// <summary>
/// Test-only base overrides (e.g. BITBROOM_TEST_LOCALAPPDATA=C:\sandbox) used by the
/// integration test harness to point categories at a sandbox. All guard rules still apply.
/// </summary>
static Dictionary<KnownBase, string?>? TestBaseOverrides()
{
    Dictionary<KnownBase, string?>? overrides = null;

    void Check(string variable, KnownBase @base)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            overrides ??= [];
            overrides[@base] = value;
            Console.Error.WriteLine($"TEST OVERRIDE ACTIVE: {@base} → {value}");
        }
    }

    Check("BITBROOM_TEST_LOCALAPPDATA", KnownBase.LocalAppData);
    Check("BITBROOM_TEST_ROAMINGAPPDATA", KnownBase.RoamingAppData);
    Check("BITBROOM_TEST_PROGRAMDATA", KnownBase.ProgramData);
    return overrides;
}

static async Task<int> CommandCleanAsync(ArgumentParser arguments, CancellationToken ct)
{
    (List<CleanCategory>? selected, int error) = SelectCategories(arguments);
    if (selected is null)
    {
        return error;
    }

    AppSettings settings = AppSettings.Load();
    if (arguments.MinAgeHours is int minAge)
    {
        settings.MinAgeHours = minAge;
    }

    bool dryRun = arguments.DryRun;
    settings.SimulateOnly = settings.SimulateOnly || dryRun;

    if (!settings.SimulateOnly && !arguments.Yes)
    {
        Console.Error.WriteLine("Refusing to delete without --yes. Use --dry-run to preview.");
        return ExitCodes.BadArguments;
    }

    // Advanced categories are never cleaned from the CLI unless individually named.
    if (!arguments.ExplicitCategories)
    {
        selected = [.. selected.Where(c => c.Risk != RiskLevel.Advanced)];
    }

    if (!ElevationInfo.IsElevated && selected.Count > 0 && selected.All(c => c.RequiresAdmin))
    {
        Console.Error.WriteLine("All selected categories require administrator rights. Re-run from an elevated terminal.");
        return ExitCodes.NeedsAdmin;
    }

    WarnAboutAdminCategories(selected);

    Dictionary<KnownBase, string?>? overrides = TestBaseOverrides();

    var engine = new CleaningEngine();
    if (!arguments.Json)
    {
        Console.WriteLine($"Scanning {selected.Count} categories…");
    }

    Dictionary<string, CategoryScanResult> scans = await engine.ScanAsync(selected, settings, baseOverrides: overrides, cancellationToken: ct);
    long totalFound = scans.Values.Sum(s => s.TotalBytes);

    if (!arguments.Json)
    {
        Console.WriteLine($"Found {ByteFormatter.Format(totalFound)} across {scans.Values.Sum(s => s.Items.Count):N0} items.");
        Console.WriteLine(settings.SimulateOnly
            ? "SIMULATION — nothing will be deleted."
            : "Cleaning…");
    }

    using var logger = new RunLogger(AppSettings.LogsDirectory, settings.SimulateOnly ? "dryrun" : "clean");
    Dictionary<string, CategoryCleanResult> results = await engine.CleanAsync(
        selected, scans, settings, logger,
        progress: arguments.Json ? null : new Progress<CleanProgress>(p =>
            Console.Write($"\r  {ByteFormatter.Format(p.BytesFreedSoFar)} freed — {Truncate(p.CategoryName, 44),-44}")),
        baseOverrides: overrides,
        cancellationToken: ct);

    if (!arguments.Json)
    {
        Console.WriteLine();
        Console.WriteLine();
    }

    long freed = results.Values.Sum(r => r.BytesFreed);
    int locked = results.Values.Sum(r => r.Locked);
    int denied = results.Values.Sum(r => r.AccessDenied);
    var errors = results.Values.SelectMany(r => r.Errors).ToList();

    if (!settings.SimulateOnly)
    {
        AppSettings persisted = AppSettings.Load();
        persisted.LifetimeBytesFreed += freed;
        persisted.LifetimeItemsDeleted += results.Values.Sum(r => (long)r.Deleted);
        persisted.LastCleanUtc = DateTime.UtcNow;
        persisted.Save();
    }

    if (arguments.Json)
    {
        var payload = new
        {
            simulated = settings.SimulateOnly,
            bytesFreed = freed,
            deleted = results.Values.Sum(r => r.Deleted),
            locked,
            denied,
            log = logger.LogFilePath,
            categories = results.ToDictionary(kv => kv.Key, kv => new
            {
                kv.Value.BytesFreed,
                kv.Value.Deleted,
                kv.Value.Locked,
                kv.Value.AccessDenied,
                kv.Value.Missing,
                kv.Value.SkippedByGuard,
                kv.Value.EmptyDirsRemoved,
                kv.Value.Errors,
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }
    else
    {
        Console.WriteLine($"{(settings.SimulateOnly ? "Would free" : "Freed")}: {ByteFormatter.Format(freed)}   " +
                          $"items: {results.Values.Sum(r => r.Deleted):N0}   in-use skipped: {locked:N0}   access denied: {denied:N0}");
        foreach (string message in errors.Take(10))
        {
            Console.Error.WriteLine($"  ! {message}");
        }

        if (logger.LogFilePath is not null)
        {
            Console.WriteLine($"Full audit log: {logger.LogFilePath}");
        }
    }

    return locked + denied > 0 || errors.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
}

static async Task<int> CommandHogsAsync(ArgumentParser arguments, CancellationToken ct)
{
    if (!arguments.Json)
    {
        Console.WriteLine("Inspecting hidden space hogs (this can take a minute on big drives)…");
        Console.WriteLine();
    }

    var inspector = new SpaceHogInspector();
    List<HogItem> hogs = await inspector.InspectAsync(ct);

    if (arguments.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(hogs.Select(h => new
        {
            h.Id,
            h.Title,
            h.SizeBytes,
            Severity = h.Severity.ToString(),
            h.Detail,
            h.Guidance,
            h.Path,
        }), JsonOptions));
        return ExitCodes.Ok;
    }

    if (hogs.Count == 0)
    {
        Console.WriteLine("Nothing notable found — no oversized hidden consumers detected.");
        return ExitCodes.Ok;
    }

    foreach (HogItem hog in hogs)
    {
        string badge = hog.Severity switch
        {
            HogSeverity.Critical => "[CRITICAL]",
            HogSeverity.Notable => "[NOTABLE] ",
            _ => "[info]    ",
        };
        Console.WriteLine($"{badge} {hog.Title,-52} {ByteFormatter.Format(hog.SizeBytes),12}");
        Console.WriteLine($"           {hog.Detail.Replace("\n", "\n           ")}");
        Console.WriteLine($"           → {hog.Guidance.Replace("\n", "\n             ")}");
        Console.WriteLine();
    }

    return ExitCodes.Ok;
}

static async Task<int> CommandAnalyzeAsync(ArgumentParser arguments, CancellationToken ct)
{
    string? target = arguments.Positional.FirstOrDefault();
    if (target is null)
    {
        Console.Error.WriteLine("Usage: bitbroom-cli analyze <path> [--depth 2] [--top 25] [--json]");
        return ExitCodes.BadArguments;
    }

    if (!Directory.Exists(target))
    {
        Console.Error.WriteLine($"Directory not found: {target}");
        return ExitCodes.BadArguments;
    }

    var analyzer = new DiskAnalyzer();
    if (!arguments.Json)
    {
        Console.WriteLine($"Analyzing {target}…");
    }

    AnalyzerResult result = await analyzer.AnalyzeAsync(
        target,
        arguments.Json ? null : new Progress<AnalyzerProgress>(p =>
            Console.Write($"\r  {p.FilesScanned:N0} files, {ByteFormatter.Format(p.BytesSoFar)}…")),
        ct);

    if (!arguments.Json)
    {
        Console.WriteLine();
        Console.WriteLine();
    }

    if (arguments.Json)
    {
        var payload = new
        {
            path = result.Root.FullPath,
            totalBytes = result.TotalBytes,
            totalFiles = result.TotalFiles,
            inaccessibleDirectories = result.InaccessibleDirectories,
            skippedReparsePoints = result.SkippedReparsePoints,
            durationMs = (long)result.Duration.TotalMilliseconds,
            tree = ToJsonNode(result.Root, arguments.Depth),
            largestFiles = result.LargestFiles.Take(arguments.Top).Select(f => new { f.Path, f.SizeBytes }),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return ExitCodes.Ok;
    }

    Console.WriteLine($"Total: {ByteFormatter.Format(result.TotalBytes)} in {result.TotalFiles:N0} files " +
                      $"({result.Duration.TotalSeconds:0.0}s, {result.InaccessibleDirectories} inaccessible, {result.SkippedReparsePoints} junctions skipped)");
    Console.WriteLine();
    PrintTree(result.Root, arguments.Depth, 0, result.TotalBytes);

    Console.WriteLine();
    Console.WriteLine($"LARGEST {Math.Min(arguments.Top, result.LargestFiles.Count)} FILES");
    foreach (LargeFile file in result.LargestFiles.Take(arguments.Top))
    {
        Console.WriteLine($"  {ByteFormatter.Format(file.SizeBytes),10}  {file.Path}");
    }

    return ExitCodes.Ok;
}

// ===========================================================================
// Helpers
// ===========================================================================

static (List<CleanCategory>? Selected, int Error) SelectCategories(ArgumentParser arguments)
{
    IReadOnlyList<CleanCategory> catalog = CategoryCatalog.Build();

    if (arguments.CategoryIds is { Count: > 0 } ids)
    {
        var byId = catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var selected = new List<CleanCategory>();
        foreach (string id in ids)
        {
            if (!byId.TryGetValue(id, out CleanCategory? category))
            {
                Console.Error.WriteLine($"Unknown category '{id}'. Run 'bitbroom-cli list'.");
                return (null, ExitCodes.BadArguments);
            }

            selected.Add(category);
        }

        return (selected, ExitCodes.Ok);
    }

    if (arguments.All)
    {
        return ([.. catalog.Where(c => c.Risk != RiskLevel.Advanced)], ExitCodes.Ok);
    }

    return ([.. catalog.Where(c => c.EnabledByDefault)], ExitCodes.Ok);
}

static void WarnAboutAdminCategories(List<CleanCategory> selected)
{
    if (ElevationInfo.IsElevated)
    {
        return;
    }

    var adminOnly = selected.Where(c => c.RequiresAdmin).Select(c => c.Id).ToList();
    if (adminOnly.Count > 0)
    {
        Console.Error.WriteLine($"Note: not running as administrator — these categories will find little or nothing: {string.Join(", ", adminOnly)}");
    }
}

static void PrintScanResults(List<CleanCategory> selected, Dictionary<string, CategoryScanResult> scans, bool json)
{
    if (json)
    {
        var payload = selected
            .Where(c => scans.ContainsKey(c.Id))
            .Select(c =>
            {
                CategoryScanResult scan = scans[c.Id];
                return new
                {
                    c.Id,
                    c.Name,
                    Risk = c.Risk.ToString(),
                    scan.TotalBytes,
                    Files = scan.FileCount,
                    scan.NotDetected,
                    scan.SkippedReparsePoints,
                    scan.SkippedCloudPlaceholders,
                    scan.SkippedTooNew,
                    scan.Inaccessible,
                    scan.Errors,
                };
            });
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return;
    }

    long total = 0;
    Console.WriteLine($"{"CATEGORY",-40} {"SIZE",12} {"FILES",10}  NOTES");
    Console.WriteLine(new string('─', 92));
    foreach (CleanCategory category in selected.OrderByDescending(c => scans.TryGetValue(c.Id, out CategoryScanResult? s) ? s.TotalBytes : 0))
    {
        if (!scans.TryGetValue(category.Id, out CategoryScanResult? scan))
        {
            continue;
        }

        if (scan.NotDetected)
        {
            continue;
        }

        total += scan.TotalBytes;
        var notes = new List<string>();
        if (scan.SkippedTooNew > 0)
        {
            notes.Add($"{scan.SkippedTooNew} too new");
        }

        if (scan.SkippedReparsePoints > 0)
        {
            notes.Add($"{scan.SkippedReparsePoints} junctions skipped");
        }

        if (scan.Errors.Count > 0)
        {
            notes.Add($"{scan.Errors.Count} errors");
        }

        Console.WriteLine($"{Truncate(category.Name, 40),-40} {ByteFormatter.Format(scan.TotalBytes),12} {scan.FileCount,10:N0}  {string.Join(", ", notes)}");
    }

    Console.WriteLine(new string('─', 92));
    Console.WriteLine($"{"TOTAL RECLAIMABLE",-40} {ByteFormatter.Format(total),12}");
    Console.WriteLine();
    Console.WriteLine("Read-only scan — nothing was deleted. Use 'bitbroom-cli clean --dry-run' to preview deletions.");
}

static void PrintTree(AnalyzerNode node, int maxDepth, int depth, long totalBytes)
{
    if (depth > maxDepth)
    {
        return;
    }

    double share = totalBytes > 0 ? node.SizeBytes * 100.0 / totalBytes : 0;
    string bar = new('█', (int)Math.Round(share / 5));
    Console.WriteLine($"{new string(' ', depth * 2)}{ByteFormatter.Format(node.SizeBytes),10}  {share,5:0.0}% {bar,-20} {node.Name}{(node.WasInaccessible ? "  (partial: access denied)" : string.Empty)}");

    foreach (AnalyzerNode child in node.Children.Take(depth == 0 ? 15 : 8))
    {
        PrintTree(child, maxDepth, depth + 1, totalBytes);
    }
}

static object ToJsonNode(AnalyzerNode node, int depth) => new
{
    name = node.Name,
    path = node.FullPath,
    sizeBytes = node.SizeBytes,
    files = node.FileCount,
    children = depth <= 0
        ? []
        : node.Children.Take(25).Select(c => ToJsonNode(c, depth - 1)).ToArray(),
};

static string Truncate(string text, int max)
    => text.Length <= max ? text : text[..(max - 1)] + "…";

internal partial class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Fatal = 1;
    public const int Partial = 2;
    public const int BadArguments = 3;
    public const int NeedsAdmin = 4;
    public const int Aborted = 5;
}

internal static class VersionInfo
{
    public static string Version =>
        typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
