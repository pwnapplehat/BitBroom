using System.Text;

namespace BitBroom.Core.Special;

public enum ScheduleFrequency
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
}

public sealed record ScheduleState(bool Exists, string? Summary);

/// <summary>
/// Windows Task Scheduler integration for automatic cleaning. Registers a per-user task
/// (no elevation, no stored password — runs under the interactive token when you're logged
/// on) that runs the CLI's default safe set: 'bitbroom-cli clean --yes'. Admin-only
/// categories are simply skipped when it runs unelevated — the CLI handles that.
///
/// BitBroom itself does NOT need to be running and needs no startup entry: the Windows
/// Task Scheduler service launches the CLI at the scheduled time on its own. The task is
/// registered from an XML definition so it is reliable in the real world — it runs on
/// battery and catches up a missed run (StartWhenAvailable) if the PC was off/asleep at
/// the scheduled time, rather than silently waiting a whole cycle.
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
        string xml = BuildTaskXml(cli, frequency, weeklyDay, hour);

        // Register from an XML file: the only way to set StartWhenAvailable / battery
        // behaviour that plain 'schtasks /Create' switches cannot express.
        string xmlPath = Path.Combine(Path.GetTempPath(), $"bitbroom-task-{Guid.NewGuid():N}.xml");
        try
        {
            // Task Scheduler XML must be UTF-16 with a BOM.
            await File.WriteAllTextAsync(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true), ct)
                .ConfigureAwait(false);

            string arguments = $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F";
            ProcessResult result = await ProcessRunner.RunAsync("schtasks.exe", arguments, TimeSpan.FromSeconds(30), null, ct)
                .ConfigureAwait(false);

            return result.Success
                ? (true, $"Scheduled: {Describe(frequency, weeklyDay, hour)}. Runs even if BitBroom is closed; no startup entry needed.")
                : (false, $"Task Scheduler refused: {FirstLine(result.Error, result.Output)}");
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }

    /// <summary>
    /// Builds a Task Scheduler 1.2 XML definition: interactive per-user token (no stored
    /// password, no elevation), runs on battery, and catches up a missed run when the PC
    /// is next available.
    /// </summary>
    private static string BuildTaskXml(string cliPath, ScheduleFrequency frequency, DayOfWeek weeklyDay, int hour)
    {
        // A fixed, past start date keeps the trigger valid; only the time-of-day matters.
        string start = $"2020-01-01T{hour:00}:00:00";
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";

        string trigger = frequency switch
        {
            ScheduleFrequency.Daily =>
                $"""
                    <CalendarTrigger>
                      <StartBoundary>{start}</StartBoundary>
                      <Enabled>true</Enabled>
                      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                    </CalendarTrigger>
                """,
            ScheduleFrequency.Monthly =>
                $"""
                    <CalendarTrigger>
                      <StartBoundary>{start}</StartBoundary>
                      <Enabled>true</Enabled>
                      <ScheduleByMonth>
                        <DaysOfMonth><Day>1</Day></DaysOfMonth>
                        <Months><January/><February/><March/><April/><May/><June/><July/><August/><September/><October/><November/><December/></Months>
                      </ScheduleByMonth>
                    </CalendarTrigger>
                """,
            _ =>
                $"""
                    <CalendarTrigger>
                      <StartBoundary>{start}</StartBoundary>
                      <Enabled>true</Enabled>
                      <ScheduleByWeek>
                        <DaysOfWeek><{DayElement(weeklyDay)}/></DaysOfWeek>
                        <WeeksInterval>1</WeeksInterval>
                      </ScheduleByWeek>
                    </CalendarTrigger>
                """,
        };

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>BitBroom automatic clean of the safe default category set.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
            {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{XmlEscape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{XmlEscape(cliPath)}</Command>
                  <Arguments>clean --yes</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string DayElement(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Monday",
        DayOfWeek.Tuesday => "Tuesday",
        DayOfWeek.Wednesday => "Wednesday",
        DayOfWeek.Thursday => "Thursday",
        DayOfWeek.Friday => "Friday",
        DayOfWeek.Saturday => "Saturday",
        _ => "Sunday",
    };

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

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
