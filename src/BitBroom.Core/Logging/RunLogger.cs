using System.Text;

namespace BitBroom.Core.Logging;

/// <summary>
/// Thread-safe, per-run log file writer. Every deletion (or simulated deletion) is recorded
/// with its full path and size so users can audit exactly what BitBroom did.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public string? LogFilePath { get; }

    public RunLogger(string logsDirectory, string runKind)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            string fileName = $"bitbroom-{runKind}-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            LogFilePath = Path.Combine(logsDirectory, fileName);
            _writer = new StreamWriter(LogFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            Info($"BitBroom {runKind} log started. Machine time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception)
        {
            // Logging is best-effort; a locked/failed log file must never block cleaning.
            _writer = null;
            LogFilePath = null;
        }
    }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message) => Write("ERROR", message);

    public void Deleted(string path, long sizeBytes, bool simulated)
        => Write(simulated ? "WOULD" : "DEL  ", $"{sizeBytes,12}  {path}");

    public void Skipped(string path, string reason) => Write("SKIP ", $"{reason,-12}  {path}");

    private void Write(string level, string message)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
            }
            catch (Exception)
            {
                // Ignore log write failures.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}
