using BitBroom.Core.Settings;

namespace BitBroom.Core.Special;

public enum ScheduleFrequency
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
}

public sealed record ScheduleState(bool Exists, string? Summary);

/// <summary>
/// Windows Task Scheduler integration for automatic cleaning. Creates a per-user task
/// (no elevation required) that runs the CLI's default safe set with the user's saved
/// settings: 'bitbroom-cli clean --yes'. Admin-only categories are simply skipped when
/// the task runs unelevated — the CLI already handles that. schtasks.exe is the
/// documented, supported interface; no COM interop, no third-party dependency.
/// </summary>
public static class ScheduledCleaning
{
    public const string TaskName = "BitBroom automatic clean";

    /// <summary>Full path to bitbroom-cli.exe next to the current executable, or null.</summary>
    public static string? FindCliPath()
    {
        try
        {
            string? directory = Path.GetDirectoryName(Environment.ProcessPath);
            if (directory is null)
            {
                return null;
            }

            string candidate = Path.Combine(directory, "bitbroom-cli.exe");
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<(bool Success, string Message)> CreateOrUpdateAsync(
        ScheduleFrequency frequency, DayOfWeek weeklyDay, int hour, CancellationToken ct = default)
    {
        string? cli = FindCliPath();
        if (cli is null)
        {
            return (false, "bitbroom-cli.exe was not found next to the app — scheduling needs the full install (or portable folder).");
        }

        hour = Math.Clamp(hour, 0, 23);
        string time = $"{hour:00}:00";

        string schedule = frequency switch
        {
            ScheduleFrequency.Daily => "/SC DAILY",
            ScheduleFrequency.Weekly => $"/SC WEEKLY /D {DayFlag(weeklyDay)}",
            ScheduleFrequency.Monthly => "/SC MONTHLY /D 1",
            _ => "/SC WEEKLY /D SUN",
        };

        // /F replaces an existing task of the same name; per-user (no /RU, no /RL HIGHEST).
        string arguments =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{cli}\\\" clean --yes\" {schedule} /ST {time} /F";

        ProcessResult result = await ProcessRunner.RunAsync("schtasks.exe", arguments, TimeSpan.FromSeconds(30), null, ct)
            .ConfigureAwait(false);

        return result.Success
            ? (true, $"Scheduled: {Describe(frequency, weeklyDay, hour)}.")
            : (false, $"Task Scheduler refused: {FirstLine(result.Error, result.Output)}");
    }

    public static async Task<(bool Success, string Message)> RemoveAsync(CancellationToken ct = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", TimeSpan.FromSeconds(30), null, ct)
            .ConfigureAwait(false);

        // "cannot find the file/task" means it is already gone — that is success for removal.
        if (result.Success || result.Error.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Scheduled cleaning removed.");
        }

        return (false, $"Task Scheduler refused: {FirstLine(result.Error, result.Output)}");
    }

    public static async Task<ScheduleState> QueryAsync(CancellationToken ct = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "schtasks.exe", $"/Query /TN \"{TaskName}\" /FO LIST", TimeSpan.FromSeconds(30), null, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return new ScheduleState(false, null);
        }

        // Surface the "Next Run Time" line when present; fall back to a generic note.
        foreach (string line in result.Output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Next Run Time:", StringComparison.OrdinalIgnoreCase))
            {
                return new ScheduleState(true, trimmed);
            }
        }

        return new ScheduleState(true, "Task registered.");
    }

    public static string Describe(ScheduleFrequency frequency, DayOfWeek weeklyDay, int hour)
    {
        string time = $"{Math.Clamp(hour, 0, 23):00}:00";
        return frequency switch
        {
            ScheduleFrequency.Daily => $"every day at {time}",
            ScheduleFrequency.Weekly => $"every {weeklyDay} at {time}",
            ScheduleFrequency.Monthly => $"on the 1st of every month at {time}",
            _ => time,
        };
    }

    private static string DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN",
    };

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

        return "unknown error";
    }
}
