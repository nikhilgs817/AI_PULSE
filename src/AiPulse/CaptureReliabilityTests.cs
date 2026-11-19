using System.Text.Json.Nodes;

namespace AiPulse;

internal static class CaptureReliabilityTests
{
    sealed class FakeBackend : IRelayBackend
    {
        public bool HasExited { get; set; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public static void Run(string root, Action<bool, string> check)
    {
        // ---- Relay backend supervision ----
        var launched = new List<FakeBackend>();
        bool healthy = false;
        var supervisor = new RelayBackendSupervisor(() => healthy, () => { var b = new FakeBackend(); launched.Add(b); return b; });
        var t = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        supervisor.Tick(t);
        check(launched.Count == 1 && supervisor.State == "Running", "Relay launches its backend on first tick");
        launched[0].HasExited = true;
        supervisor.Tick(t.AddSeconds(5));
        check(launched.Count == 2 && launched[0].Disposed && supervisor.Restarts == 1 && supervisor.Healthy,
            "A backend that exits is restarted instead of ending the relay");
        launched[1].HasExited = true;
        supervisor.Tick(t.AddSeconds(6));
        check(launched.Count == 2 && supervisor.State.StartsWith("Restarting"), "Repeated backend exits back off instead of crash-looping");
        supervisor.Tick(t.AddSeconds(30));
        check(launched.Count == 3, "Backend restarts again after the back-off delay");
        launched[2].HasExited = true; healthy = true;
        supervisor.Tick(t.AddSeconds(31));
        check(launched.Count == 3 && supervisor.State == "Adopted", "A healthy backend already on the private port is adopted, not duplicated");
        var failing = new RelayBackendSupervisor(() => false, () => throw new IOException("missing"));
        failing.Tick(t);
        check(!failing.Healthy && failing.State.Contains("retrying"), "A backend that cannot start is reported and retried");

        // ---- Backend log privacy filter ----
        check(BackendLogWriter.Keep("[GIN] 2026/11/18 - 00:53:45 | 200 |  1.2s |  127.0.0.1 | POST \"/api/chat\"") &&
              BackendLogWriter.Keep("time=x level=ERROR source=server.go msg=\"llama runner terminated\"") &&
              !BackendLogWriter.Keep("time=x level=DEBUG msg=\"chat request\" prompt=\"my secret prompt\""),
            "Backend log keeps request metadata and errors but drops debug output that can contain prompts");

        // ---- Ollama request log ----
        var lines = """
            [GIN] 2026/11/18 - 00:53:45 | 200 |       0s |       127.0.0.1 | GET      "/api/ps"
            [GIN] 2026/11/18 - 00:53:49 | 200 |  2.5123s |       127.0.0.1 | POST     "/v1/chat/completions"
            [GIN] 2026/11/18 - 00:54:10 | 500 |  1m2.5s |       127.0.0.1 | POST     "/api/chat"
            time=2026-11-18T00:53:49 level=INFO msg="unrelated"
            """;
        var parsed = OllamaServerLog.Parse(lines);
        check(parsed.Count == 3 && parsed.Count(OllamaServerLog.IsInference) == 2 && Math.Abs(parsed[1].Seconds - 2.5123) < 1e-9 &&
              Math.Abs(parsed[2].Seconds - 62.5) < 1e-9 && parsed[2].Status == 500, "Ollama [GIN] request lines parse status, latency and inference paths");
        check(OllamaServerLog.GoDuration("512.3µs") is double us && Math.Abs(us - 512.3e-6) < 1e-12 &&
              OllamaServerLog.GoDuration("45.1ms") is double ms && Math.Abs(ms - 0.0451) < 1e-12 && OllamaServerLog.GoDuration("abc") is null,
            "Go duration strings convert to seconds");
        string logFile = Path.Combine(root, "server.log");
        File.WriteAllText(logFile, lines.Replace("\r\n", "\n") + "\n");
        var reader = new OllamaServerLog(() => [logFile]);
        reader.Poll();
        int first = reader.Since(0).Count;
        File.AppendAllText(logFile, "[GIN] 2026/11/18 - 00:55:00 | 200 |  1s |  127.0.0.1 | POST \"/api/generate\"\n");
        reader.Poll();
        check(first == 3 && reader.Since(0).Count == 4, "Request log is tailed incrementally without re-reading old lines");
        File.WriteAllText(logFile, "[GIN] 2026/11/18 - 01:00:00 | 200 |  1s |  127.0.0.1 | POST \"/api/generate\"\n");
        reader.Poll();
        check(reader.Since(0).Count == 5, "A rotated (shorter) request log is read from the start");

        // ---- Codex executable resolution ----
        string bin = Path.Combine(root, "codex-bin");
        Directory.CreateDirectory(bin);
        check(CodexLiveQuota.Resolve(bin, ".EXE;.CMD", "") is null, "A missing codex command resolves to nothing instead of failing every minute");
        string shim = Path.Combine(bin, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        File.WriteAllText(shim, "");
        check(string.Equals(CodexLiveQuota.Resolve("relative-dir" + Path.PathSeparator + bin, ".EXE;.CMD", ""), shim, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "An npm codex shim on PATH is found; relative PATH entries are ignored");
        string custom = Path.Combine(root, "my-codex.exe");
        File.WriteAllText(custom, "");
        check(CodexLiveQuota.Resolve("", ".EXE", custom) == custom, "AIPULSE_CODEX overrides PATH lookup");

        // ---- Claude status-line setup ----
        string claudeDir = Path.Combine(root, "claude-config");
        string settings = ClaudeStatusLineSetup.SettingsPath(claudeDir);
        check(ClaudeStatusLineSetup.Inspect(settings) == ClaudeStatusLineSetup.State.NotConfigured, "Missing Claude settings read as not configured");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(settings, """{"model":"opus","permissions":{"allow":["Bash(ls)"]}}""");
        var state = ClaudeStatusLineSetup.Connect(settings, @"C:\Apps\AI Pulse\AiPulse.exe");
        var saved = JsonNode.Parse(File.ReadAllText(settings))!;
        check(state == ClaudeStatusLineSetup.State.Connected && saved["model"]!.GetValue<string>() == "opus" &&
              saved["permissions"]!["allow"]![0]!.GetValue<string>() == "Bash(ls)" &&
              saved["statusLine"]!["command"]!.GetValue<string>() == "\"C:/Apps/AI Pulse/AiPulse.exe\" --claude-quota-statusline" &&
              File.Exists(settings + ".aipulse-backup"),
            "Connecting the Claude status line keeps every other setting and leaves a backup");
        File.WriteAllText(settings, """{"statusLine":{"type":"command","command":"my-own-line.sh"}}""");
        check(ClaudeStatusLineSetup.Connect(settings, @"C:\x\AiPulse.exe") == ClaudeStatusLineSetup.State.OtherStatusLine &&
              File.ReadAllText(settings).Contains("my-own-line.sh"), "An existing user status line is never replaced");
    }
}
