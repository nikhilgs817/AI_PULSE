using System.Diagnostics;
using System.Text.Json;

namespace AiPulse;

internal static class CodexLiveQuota
{
    // Store/desktop Codex installs do not put a `codex` command on PATH. Spawning the bare name then
    // failed every minute; resolve it explicitly (AIPULSE_CODEX overrides) and report absence instead.
    internal static string? Resolve(string? path = null, string? pathExt = null, string? overrideFile = null)
    {
        overrideFile ??= Environment.GetEnvironmentVariable("AIPULSE_CODEX");
        if (!string.IsNullOrWhiteSpace(overrideFile) && Path.IsPathFullyQualified(overrideFile) && File.Exists(overrideFile)) return overrideFile;
        string[] extensions = OperatingSystem.IsWindows()
            ? (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Equals(".exe", StringComparison.OrdinalIgnoreCase) || e.Equals(".cmd", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [""];
        foreach (var directory in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string folder = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue; // never resolve from the working directory
            foreach (var extension in extensions)
            {
                string candidate = Path.Combine(folder, "codex" + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    internal static async Task<ProviderQuota> Read()
    {
        string executable = Resolve() ?? throw new FileNotFoundException("No codex command on PATH.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        try
        {
            process.Start();
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"aipulse\",\"version\":\"0.3.6\"}}}");
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out int number)) continue;
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex rejected the usage request. Check Codex sign-in.");
                if (number == 1)
                {
                    await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
                    await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                }
                if (number == 2) return Parse(root.GetProperty("result"), DateTimeOffset.UtcNow);
            }
            throw new IOException("Codex closed before reporting usage.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    internal static ProviderQuota Parse(JsonElement result, DateTimeOffset now)
    {
        var limits = result.GetProperty("rateLimits");
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.TryGetProperty("codex", out var codex)) limits = codex;
        QuotaWindow? shortWindow = null, weekly = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            double used = window.GetProperty("usedPercent").GetDouble();
            int minutes = window.GetProperty("windowDurationMins").GetInt32();
            var reset = DateTimeOffset.FromUnixTimeSeconds(window.GetProperty("resetsAt").GetInt64());
            if (!double.IsFinite(used) || used < 0 || reset <= now) continue;
            var reading = new QuotaWindow(Math.Min(100, used), reset, now);
            if (minutes == 300) shortWindow = reading;
            if (minutes == 10080) weekly = reading;
        }
        return new(shortWindow, weekly, now);
    }
}
