using System.Diagnostics;

namespace AiPulse;

public interface IRelayBackend : IDisposable
{
    bool HasExited { get; }
}

// Ollama writes its request log ([GIN] lines) and crash reasons to stderr. The relay-launched backend
// has no console, so these were lost; keep them in a size-capped file the dashboard can read.
sealed class BackendLogWriter
{
    const long MaxBytes = 10 * 1024 * 1024;
    readonly string path;
    readonly object gate = new();
    public BackendLogWriter(string path) { this.path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
    public void Attach(Process process)
    {
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.BeginErrorReadLine(); process.BeginOutputReadLine();
    }
    // Keep request metadata and failure reasons only; debug output can contain prompt text.
    internal static bool Keep(string line) =>
        line.StartsWith("[GIN] ", StringComparison.Ordinal) || line.StartsWith("Error:", StringComparison.Ordinal) ||
        line.Contains("level=ERROR", StringComparison.Ordinal) || line.Contains("level=WARN", StringComparison.Ordinal) ||
        line.Contains("msg=\"Listening on", StringComparison.Ordinal);
    void Write(string? line)
    {
        if (line is null || !Keep(line)) return;
        if (line.Length > 500) line = line[..500];
        lock (gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

sealed class ProcessRelayBackend(Process process) : IRelayBackend
{
    public bool HasExited => process.HasExited;
    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}

// Keeps the relay's private model-server backend alive. Ollama tray updates, crashes and manual
// kills used to end the relay (and with it all capture until the next sign-in). Now an exited
// backend is restarted with capped exponential back-off, and an already-healthy backend on the
// private port (for example one started by a previous relay) is adopted instead of duplicated.
public sealed class RelayBackendSupervisor(Func<bool> backendHealthy, Func<IRelayBackend?> launch) : IDisposable
{
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(60), StableAfter = TimeSpan.FromMinutes(5);
    IRelayBackend? owned;
    DateTime startedAt, nextAttempt = DateTime.MinValue;
    TimeSpan delay = FirstDelay;
    public int Restarts { get; private set; }
    public int Launches { get; private set; }
    public string State { get; private set; } = "Starting";
    public bool Healthy => State is "Running" or "Adopted";

    public void Tick(DateTime now)
    {
        if (owned is { HasExited: false })
        {
            if (now - startedAt >= StableAfter) delay = FirstDelay;
            State = "Running";
            return;
        }
        if (owned != null)
        {
            owned.Dispose(); owned = null; Restarts++;
            Paths.Note("relay-backend", "Managed model backend exited; restarting with back-off.");
        }
        bool adopted;
        try { adopted = backendHealthy(); } catch (Exception) { adopted = false; }
        if (adopted) { State = "Adopted"; return; }
        if (now < nextAttempt) { State = Restarts > 0 ? $"Restarting (attempt {Launches + 1} in {(int)Math.Ceiling((nextAttempt - now).TotalSeconds)} s)" : "Starting"; return; }
        try
        {
            owned = launch() ?? throw new IOException("The configured backend could not be started.");
            startedAt = now; Launches++; State = "Running";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Paths.Log("relay-backend", e); State = "Backend failed to start; retrying";
        }
        nextAttempt = now + delay;
        delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
    }

    public void Dispose() { owned?.Dispose(); owned = null; }
}
