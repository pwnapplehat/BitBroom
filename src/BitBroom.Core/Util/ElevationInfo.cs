using System.Diagnostics;
using System.Security.Principal;

namespace BitBroom.Core.Util;

public static class ElevationInfo
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => Elevated.Value;

    /// <summary>
    /// Relaunches the given executable with a UAC elevation prompt.
    /// Returns true when the elevated process was started (the caller should then exit).
    /// </summary>
    public static bool RelaunchAsAdministrator(string executablePath, string arguments = "")
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            return Process.Start(psi) is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the UAC prompt.
            return false;
        }
    }
}
