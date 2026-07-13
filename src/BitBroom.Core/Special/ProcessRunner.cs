using System.Diagnostics;
using System.Text;

namespace BitBroom.Core.Special;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs system utilities (DISM, powercfg, vssadmin, wevtutil, PowerShell cmdlets) with captured output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null)
    {
        // wsl.exe writes UTF-16LE to redirected pipes; everything else we run is UTF-8.
        outputEncoding ??= fileName.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Unicode
            : Encoding.UTF8;

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"Failed to start {fileName}");
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, $"Failed to start {fileName}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Process may have exited already.
            }

            return new ProcessResult(-2, output.ToString(), "Operation cancelled or timed out.");
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    public static Task<ProcessResult> RunPowerShellAsync(
        string command,
        TimeSpan? timeout = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
        => RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            timeout,
            onOutputLine,
            cancellationToken);
}
