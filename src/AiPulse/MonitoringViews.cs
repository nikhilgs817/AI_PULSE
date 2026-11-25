using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiPulse;

public sealed partial class MainWindow
{
    // ---------------------------------------------------------------- Coverage health card
    string repairNotice = "";
    bool repairArmed, repairRunning;

    internal enum Health { Receiving, Waiting, Stale, Broken, Unavailable }

    // Sources refreshed on a timer; the others only report when traffic arrives, so silence is not staleness.
    static bool Polled(string source) => source is "Persistent Ollama capture" or "Ollama request log" or "Ollama coverage" or "Codex" or "Claude Code" || source.StartsWith("vLLM /metrics");

    internal static Health Classify(UsageSourceState s, long now)
    {
        if (s.State is "Disconnected" or "Error" or "Storage error") return Health.Broken;
        if (s.State is "Needs exporter" or "No token ledger") return Health.Unavailable;
        if (Polled(s.Source) && now - s.Checked > 180) return Health.Stale; // its collector stopped reporting
        return s.State switch
        {
            "Receiving" or "Connected" or "Covered" => Health.Receiving,
            "Ready" or "Idle" => Health.Waiting,
            _ => Health.Stale, // Degraded, Gaps, Incomplete, Backfilling, No recent records
        };
    }

    static string HealthColor(Health h) => h switch { Health.Receiving => "#59dfae", Health.Waiting => "#5aa9ff", Health.Stale => "#e8b65f", Health.Broken => "#ff6b7a", _ => "#7f93af" };

    Control HealthCard()
    {
        long now = J.Now;
        var stack = new StackPanel { Spacing = 8 };
        var rows = service.Store.SourceStates().Select(s => (s.Source, Health: Classify(s, now), s.State, s.Detail, s.LastObserved)).ToList();
        if (!service.IsRemote)
        {
            var claude = ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath());
            // Configured is not the same as working: judge by when the status line last wrote a reading.
            // The heartbeat is written only by the status line itself, not by the fallback endpoint.
            long written = ClaudeQuotaStatusLine.LastSeen()?.ToUnixTimeSeconds() ?? 0;
            bool fresh = written > now - 6 * 3600;
            rows.Add(("Claude status line", claude switch { ClaudeStatusLineSetup.State.Connected => fresh ? Health.Receiving : Health.Stale, ClaudeStatusLineSetup.State.Unreadable => Health.Broken, _ => Health.Stale },
                claude switch { ClaudeStatusLineSetup.State.Connected => fresh ? "Connected" : "Configured, no recent reading", ClaudeStatusLineSetup.State.OtherStatusLine => "Your own status line", ClaudeStatusLineSetup.State.Unreadable => "Settings unreadable", _ => "Not connected" },
                claude switch
                {
                    ClaudeStatusLineSetup.State.Connected when fresh => "Claude Code passes 5-hour and weekly usage to AI Pulse after each reply. No network call or sign-in reuse.",
                    ClaudeStatusLineSetup.State.Connected => (written == 0 ? "No reading has ever arrived. " : "No reading in the last 6 hours. ") + "Status lines run only in terminal Claude Code sessions (not the desktop app or IDE panels); use Claude Code in a terminal once to refresh, otherwise the rate-limited usage endpoint is the fallback.",
                    ClaudeStatusLineSetup.State.OtherStatusLine => "Connect to share Claude limits with AI Pulse. Your own status line keeps running and showing exactly what it shows now.",
                    ClaudeStatusLineSetup.State.Unreadable => "~/.claude/settings.json could not be read as JSON.",
                    _ => "Connect it to read Claude limits reliably instead of the undocumented, rate-limited usage endpoint.",
                }, written));
            if (CodexLiveQuota.Resolve() is null)
                rows.Add(("ChatGPT / Codex live usage", Health.Unavailable, "Session logs only", "No codex command on PATH (Store/desktop install). Limits come from Codex session logs after each turn; set AIPULSE_CODEX to a codex executable for live reads.", 0L));
        }
        int broken = rows.Count(r => r.Health == Health.Broken), stale = rows.Count(r => r.Health == Health.Stale);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(Text("Coverage health", 16, null, true));
        title.Children.Add(Text(broken > 0 ? $"{broken} broken" : stale > 0 ? $"{stale} need attention" : "All sources healthy", 12, broken > 0 ? "#ff6b7a" : stale > 0 ? "#e8b65f" : "#59dfae", true));
        stack.Children.Add(title);
        foreach (var row in rows.OrderBy(r => r.Health switch { Health.Broken => 0, Health.Stale => 1, Health.Receiving => 2, Health.Waiting => 3, _ => 4 }).ThenBy(r => r.Source))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 4) };
            var dot = Text("●", 14, HealthColor(row.Health)); dot.Margin = new Thickness(0, 0, 10, 0); dot.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(dot);
            var text = new StackPanel { Spacing = 2 };
            string label = row.Health switch { Health.Receiving => "Receiving", Health.Waiting => "Waiting for traffic", Health.Stale => "Stale", Health.Broken => "Broken", _ => "Not available" };
            text.Children.Add(Text($"{row.Source} · {label} ({row.State})" + (row.LastObserved > 0 ? " · last " + DateTimeOffset.FromUnixTimeSeconds(row.LastObserved).ToLocalTime().ToString("MMM d HH:mm") : ""), 13, "#dbe5f4", true));
            text.Children.Add(Text(row.Detail, 12, "#9eb1ca"));
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            if (RepairButton(row.Source, row.Health) is { } button) { button.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(button, 2); grid.Children.Add(button); }
            stack.Children.Add(grid);
        }
        if (repairNotice != "") stack.Children.Add(Text(repairNotice, 12, repairNotice.StartsWith("Repaired") || repairNotice.StartsWith("Connected") ? "#59dfae" : "#e8b65f"));
        return Card(stack);
    }

    Button? RepairButton(string source, Health health)
    {
        if (service.IsRemote) return null;
        if (source == "Claude status line" && ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath()) is ClaudeStatusLineSetup.State.NotConfigured or ClaudeStatusLineSetup.State.OtherStatusLine)
        {
            bool wrap = ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath()) == ClaudeStatusLineSetup.State.OtherStatusLine;
            var connect = Btn("Connect");
            ToolTip.SetTip(connect, wrap
                ? "Routes your status line through AI Pulse in ~/.claude/settings.json (a backup is kept). Your command still runs and prints as before. Takes effect in new Claude Code sessions."
                : "Adds a statusLine entry to ~/.claude/settings.json (a backup is kept). Takes effect in new Claude Code sessions.");
            connect.Click += (_, _) =>
            {
                try
                {
                    var state = ClaudeStatusLineSetup.Connect(ClaudeStatusLineSetup.SettingsPath(), Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AiPulse.exe"), wrapExisting: wrap);
                    repairNotice = state == ClaudeStatusLineSetup.State.Connected ? "Connected the Claude status line. Start a new Claude Code session; readings appear after its first reply." : "Left your existing status line unchanged.";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Paths.Log("statusline-connect", e); repairNotice = "Could not update ~/.claude/settings.json."; }
                RenderBody();
            };
            return connect;
        }
        if (source == "Persistent Ollama capture" && health is Health.Broken or Health.Stale && OperatingSystem.IsWindows())
        {
            var repair = Btn(repairRunning ? "Repairing…" : repairArmed ? "Confirm: restarts Ollama" : "Repair capture");
            repair.IsEnabled = !repairRunning;
            ToolTip.SetTip(repair, "Re-runs Enable-OllamaCapture.ps1 -Repair: stops Ollama's own server and tray (unloading models), parks a restored Ollama startup entry, and restarts the relay with its watchdog.");
            repair.Click += async (_, _) =>
            {
                if (!repairArmed) { repairArmed = true; RenderBody(); return; }
                repairArmed = false; repairRunning = true; repairNotice = ""; RenderBody();
                repairNotice = await Task.Run(RunCaptureRepair);
                repairRunning = false; RenderBody();
            };
            return repair;
        }
        return null;
    }

    static string? CaptureScript() =>
        new[] { Path.Combine(AppContext.BaseDirectory, "Enable-OllamaCapture.ps1"), Path.Combine(AppContext.BaseDirectory, "docs", "telemetry", "Enable-OllamaCapture.ps1") }.FirstOrDefault(File.Exists);

    static string RunCaptureRepair()
    {
        string? script = CaptureScript();
        string? app = Environment.ProcessPath;
        if (script is null || app is null) return "Repair script not found next to AiPulse.exe. Run docs/telemetry/Enable-OllamaCapture.ps1 -Repair -RestartOllama manually.";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-App", app, "-DataDir", Paths.Root, "-Repair", "-RestartOllama" }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new IOException("powershell.exe did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return "Repair timed out after 2 minutes. Check logs/app.log."; }
            if (process.ExitCode == 0) return "Repaired: the relay owns port 11434 again and its watchdog task restarts it if it stops.";
            Paths.Note("capture-repair", "Repair script exited with code " + process.ExitCode);
            string reason = error.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("At ") && !l.StartsWith("+")) ?? "unknown error";
            return "Repair failed: " + (reason.Length > 240 ? reason[..240] : reason);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Paths.Log("capture-repair", e); return "Could not start PowerShell for the repair."; }
    }

    // ---------------------------------------------------------------- Model insights
    Control Bar(double fraction, string color, double height = 8)
    {
        fraction = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Max(fraction, 0.0001):0.####}*,{Math.Max(1 - fraction, 0.0001):0.####}*"), Height = height, MinWidth = 120 };
        grid.Children.Add(new Border { Background = Brush.Parse(color), CornerRadius = new CornerRadius(height / 2) });
        var rest = new Border { Background = Paint("#22324a", "#dfe7f3"), CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumn(rest, 1); grid.Children.Add(rest);
        return grid;
    }

    Control ModelInsights()
    {
        var stack = new StackPanel { Spacing = 16 };
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var traces = service.Store.Traces(nowMs - 3_600_000).Where(t => t.Outcome != "Agent run").ToList();

        // Context-window gauge: last request size against the loaded context length.
        var gauge = new StackPanel { Spacing = 10 };
        gauge.Children.Add(Text("Context window · last request per loaded model", 16, null, true));
        var loaded = service.Ollama.Models.Where(m => m.Context > 0).ToList();
        if (loaded.Count == 0) gauge.Children.Add(Text("No loaded model reports a context length.", 12, "#8da1bd"));
        foreach (var m in loaded)
        {
            var last = traces.Where(t => t.Model == m.Name || t.Model + ":latest" == m.Name).OrderByDescending(t => t.EndMs).FirstOrDefault();
            double used = last is null ? 0 : (double)(last.PromptTokens + last.OutputTokens) / m.Context;
            string color = used >= 0.9 ? "#ff6b7a" : used >= 0.7 ? "#e8b65f" : "#59dfae";
            var row = new StackPanel { Spacing = 4 };
            string offload = ModelResidency.CpuShare(m) is double cpu ? $" · {cpu:P0} on CPU (slower)" : "";
            row.Children.Add(Text($"{m.Name} · {m.Host}{offload}", 13, "#dbe5f4", true));
            row.Children.Add(Bar(used, color));
            row.Children.Add(Text(last is null ? $"No observed request in the last hour · context {m.Context:N0} tokens"
                : $"{last.PromptTokens + last.OutputTokens:N0} / {m.Context:N0} tokens ({used:P0}) · {DateTimeOffset.FromUnixTimeMilliseconds(last.EndMs).ToLocalTime():HH:mm:ss}" +
                  (last.PromptTokens >= m.Context * 0.98 ? " · prompt reached the limit: Ollama dropped older context" : used >= 0.9 ? " · close to the limit: older context will be truncated" : ""), 11, used >= 0.9 ? "#ff9aa5" : "#9eb1ca"));
            gauge.Children.Add(row);
        }
        stack.Children.Add(Card(gauge));

        // Speed: prompt processing vs generation, and time to first token.
        var speed = new StackPanel { Spacing = 10 };
        speed.Children.Add(Text("Speed by model · last hour", 16, null, true));
        speed.Children.Add(Text("TTFT = time to first token (prompt processing + model load). Prompt and generation rates are Ollama-reported; OpenAI-compatible endpoints report TTFT only when streaming.", 11, "#8ba0bd"));
        var rows = traces.GroupBy(t => t.Model == "" ? "(unknown)" : t.Model).OrderByDescending(g => g.Count()).Select(g =>
        {
            var list = g.ToList();
            return new[]
            {
                g.Key, list.Count.ToString("N0"), Seconds(Percentile(list.Select(t => t.TtftSeconds), 0.5)), Seconds(Percentile(list.Select(t => t.TtftSeconds), 0.9)),
                Rate(Percentile(list.Select(t => t.PromptRate), 0.5)), Rate(Percentile(list.Select(t => t.GenerateRate), 0.5)),
                Seconds(list.Where(t => t.LoadSeconds > 0.5).Select(t => t.LoadSeconds).Max()), list.Count(t => t.HttpStatus >= 400 || t.Outcome.StartsWith("Failed")).ToString("N0"),
            };
        }).ToList();
        if (rows.Count == 0) speed.Children.Add(Text("No timed requests in the last hour. Requests through the relay (port 11434) or gateway (11435) are timed.", 12, "#8da1bd"));
        else speed.Children.Add(Table(["Model", "Requests", "TTFT p50", "TTFT p90", "Prompt tok/s", "Gen tok/s", "Slowest load", "Errors"], rows, "2*,*,*,*,*,*,*,*"));
        stack.Children.Add(Card(speed));

        // VRAM residency: loads, idle unloads and evictions.
        if (!service.IsRemote)
        {
            var events = localService.Residency.Since(J.Now - 6 * 3600).OrderByDescending(e => e.Time).Take(12).ToList();
            var residency = new StackPanel { Spacing = 10 };
            int evictions = localService.Residency.Since(J.Now - ModelResidency.ThrashWindow).Count(e => e.Kind == "Evicted");
            residency.Children.Add(Text("VRAM residency · last 6 hours", 16, null, true));
            residency.Children.Add(Text(evictions >= ModelResidency.ThrashEvictions ? $"{evictions} evictions in 30 minutes: models are competing for VRAM and reloading." : "Evicted = unloaded to make room for another model. Unloaded = idle keep-alive expired (normal).", 12, evictions >= ModelResidency.ThrashEvictions ? "#ff9aa5" : "#9eb1ca"));
            if (events.Count == 0) residency.Children.Add(Text("No load or unload events since AI Pulse started.", 12, "#8da1bd"));
            else residency.Children.Add(Table(["Time", "Model", "Event", "Size", "In VRAM"], events.Select(e => new[] { DateTimeOffset.FromUnixTimeSeconds(e.Time).ToLocalTime().ToString("HH:mm:ss"), e.Model, e.Kind, J.Size(e.SizeGiB), J.Size(e.GpuGiB) }), "*,2*,*,*,*"));
            stack.Children.Add(Card(residency));
        }

        // vLLM servers read directly from /metrics.
        if (!service.IsRemote) stack.Children.Add(VllmCard());
        return stack;
    }

    Control VllmCard()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("vLLM servers", 16, null, true));
        var servers = localService.Vllm.Servers;
        if (!servers.Any(s => s.Online))
        {
            stack.Children.Add(Text($"No vLLM server answering at {string.Join(", ", localService.Settings.VllmEndpoints.DefaultIfEmpty("(none configured)"))}. Add your servers to VllmEndpoints in settings.json (for k3s, the Service or NodePort URL). Queue depth, KV cache, TTFT and token counters are read from /metrics with no Collector.", 12, "#8da1bd"));
            return Card(stack);
        }
        foreach (var s in servers.Where(s => s.Online))
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Text($"{s.Model} · {new Uri(s.Url).Authority}" + (s.MaxModelLen is long len ? $" · max context {len:N0}" : ""), 13, "#dbe5f4", true));
            row.Children.Add(Text($"Running {s.Running:N0} · waiting {s.Waiting:N0} · TTFT {Seconds(s.TtftSeconds)} · latency {Seconds(s.LatencySeconds)} · prompt {Rate(s.PromptTps)} tok/s · generation {Rate(s.GenerateTps)} tok/s" + (s.Preemptions > 0 ? $" · {s.Preemptions:N0} preemptions" : ""), 12, s.Waiting > 0 || s.Preemptions > 0 ? "#e8b65f" : "#9eb1ca"));
            if (s.KvCache is double kv) { row.Children.Add(Bar(kv, kv >= 0.9 ? "#ff6b7a" : kv >= 0.7 ? "#e8b65f" : "#59dfae")); row.Children.Add(Text($"KV cache {kv:P0} used", 11, "#9eb1ca")); }
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    static double? Percentile(IEnumerable<double?> values, double p)
    {
        var sorted = values.Where(v => v is double d && double.IsFinite(d)).Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
    static string Seconds(double? s) => s is double v ? v < 1 ? $"{v * 1000:0} ms" : $"{v:0.0} s" : "—";
    static string Rate(double? r) => r is double v ? v.ToString(v >= 100 ? "0" : "0.0") : "—";

    // ---------------------------------------------------------------- Agents: live, timeline, labs
    int timelineHours = 1;
    string labName = "";
    LabSessions? labSessions;
    LabSessions Labs => labSessions ??= LabSessions.Shared;

    Control AgentActivity()
    {
        var stack = new StackPanel { Spacing = 16 };
        if (!service.IsRemote) { stack.Children.Add(LabCard()); stack.Children.Add(BenchCard()); }
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Working now: requests in flight through the gateway or relay, plus agents active in the last minute.
        var live = new StackPanel { Spacing = 8 };
        List<InFlightRequest> inFlight = service.IsRemote ? [] : localService.Gateway.InFlight.Values.Concat(localService.Relay.InFlight).ToList();
        var recent = service.Store.Traces(nowMs - 60_000).Where(t => t.Agent != "").Select(t => t.Agent).Distinct().ToList();
        var agentsNow = inFlight.Where(r => r.Agent != "").Select(r => r.Agent).Union(recent).ToList();
        live.Children.Add(Text($"Working now · {agentsNow.Count} agent{(agentsNow.Count == 1 ? "" : "s")}, {inFlight.Count} model call{(inFlight.Count == 1 ? "" : "s")} in progress", 16, null, true));
        if (inFlight.Count == 0 && recent.Count == 0)
            live.Children.Add(Text($"Nothing in progress. Agents are named when they send {Gateway.AgentHeader} (and optionally {Gateway.RunHeader}) on model requests, or export OpenTelemetry agent spans to 127.0.0.1:{localService.Settings.TelemetryPort} over HTTP.", 12, "#8da1bd"));
        else
            live.Children.Add(Table(["Agent", "Model", "Run", "Running for"], inFlight.OrderBy(r => r.StartMs).Select(r => new[] { r.Agent == "" ? "(unnamed client)" : r.Agent, r.Model, r.Run == "" ? "—" : r.Run, Seconds((nowMs - r.StartMs) / 1000.0) })
                .Concat(recent.Where(a => inFlight.All(r => r.Agent != a)).Select(a => new[] { a, "—", "—", "active in the last minute" })), "1.5*,2*,*,*"));
        stack.Children.Add(Card(live));

        // Timeline: one row per run, agent span outlined, model calls filled.
        var timeline = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(Text("Agent timeline", 16, null, true));
        foreach (int hours in new[] { 1, 6, 24 })
        {
            var b = Btn(hours == 1 ? "1 hour" : $"{hours} hours"); if (hours == timelineHours) b.Background = Selected;
            b.Click += (_, _) => { timelineHours = hours; RenderBody(); };
            header.Children.Add(b);
        }
        timeline.Children.Add(header);
        long from = nowMs - timelineHours * 3_600_000L;
        var traces = service.Store.Traces(from).ToList();
        if (traces.Count == 0) timeline.Children.Add(Text("No timed requests in this window.", 12, "#8da1bd"));
        else
        {
            timeline.Children.Add(new AgentTimeline { Traces = traces, From = from, To = nowMs, Height = AgentTimeline.HeightFor(traces), LightTheme = LightTheme });
            var runs = AgentTimeline.Rows(traces).Take(30).Select(r => new[]
            {
                r.Label, r.Calls.Count.ToString("N0"), $"{r.Calls.Sum(c => c.PromptTokens):N0} / {r.Calls.Sum(c => c.OutputTokens):N0}",
                Seconds((r.End - r.Start) / 1000.0), string.Join(", ", r.Calls.Select(c => c.Model).Where(m => m != "").Distinct().Take(3)),
            });
            timeline.Children.Add(Table(["Agent · run", "Model calls", "Tokens in / out", "Duration", "Models"], runs, "2*,*,1.4*,*,2*"));
        }
        stack.Children.Add(Card(timeline));
        return stack;
    }

    Control LabCard()
    {
        var stack = new StackPanel { Spacing = 10 };
        var current = Labs.Current;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(Text("Lab session", 16, null, true));
        if (current is null)
        {
            var name = new TextBox { Text = labName, PlaceholderText = "Name, e.g. Phoenix agents run 3", Width = 280 };
            Avalonia.Automation.AutomationProperties.SetName(name, "Lab session name");
            name.TextChanged += (_, _) => labName = name.Text ?? "";
            var start = Btn("● Start recording");
            start.Click += (_, _) => { Labs.Start(labName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); labName = ""; RenderBody(); };
            row.Children.Add(name); row.Children.Add(start);
            stack.Children.Add(row);
            stack.Children.Add(Text("Start before a lab, stop after it: you get a report of tokens per model and agent, TTFT and tokens/s, errors, VRAM peaks, model swaps and the equivalent API cost.", 12, "#8ba0bd"));
        }
        else
        {
            var stop = Btn("■ Stop and build report");
            stop.Click += async (_, _) =>
            {
                stop.IsEnabled = false;
                var lab = await Task.Run(() => Labs.Stop(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WriteLabReport));
                notice = lab?.Report is { } path ? "Lab report saved: " + path : "Lab stopped; the report could not be written (see logs/app.log).";
                if (lab?.Report is { } file) OpenFile(file);
                RenderBody();
            };
            row.Children.Add(Text($"● Recording “{current.Name}” since {DateTimeOffset.FromUnixTimeMilliseconds(current.StartMs).ToLocalTime():HH:mm} ({TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - current.StartMs):h\\:mm\\:ss})", 13, "#ff6b7a", true));
            row.Children.Add(stop);
            stack.Children.Add(row);
        }
        var finished = Labs.All().Where(l => l.EndMs is not null).ToList();
        if (finished.Count >= 2)
            stack.Children.Add(Text("Compare runs a lab against the one before it: tokens, cache hits, latency, errors, cost, VRAM and energy per token, with what got better or worse.", 12, "#8ba0bd"));
        foreach (var lab in Labs.All().Where(l => l.EndMs is not null).Reverse().Take(5))
        {
            var past = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            past.Children.Add(Text($"{lab.Name} · {DateTimeOffset.FromUnixTimeMilliseconds(lab.StartMs).ToLocalTime():MMM d HH:mm} · {TimeSpan.FromMilliseconds(lab.EndMs!.Value - lab.StartMs):h\\:mm\\:ss}", 12, "#b3c8e2"));
            if (lab.Report is { } file && File.Exists(file)) { var open = Btn("Open report"); open.Click += (_, _) => OpenFile(file); past.Children.Add(open); }
            var rebuild = Btn("Rebuild");
            ToolTip.SetTip(rebuild, "Rebuild the report from retained data (for example after editing lab-pricing.json).");
            rebuild.Click += async (_, _) => { var path = await Task.Run(() => WriteLabReport(lab)); OpenFile(path); };
            past.Children.Add(rebuild);
            int position = finished.FindIndex(l => l.Id == lab.Id);
            if (position > 0)
            {
                var baseline = finished[position - 1];
                var compare = Btn("Compare with previous");
                ToolTip.SetTip(compare, $"Before/after report: “{lab.Name}” against “{baseline.Name}”.");
                compare.Click += async (_, _) =>
                {
                    try
                    {
                        compare.IsEnabled = false;
                        var path = await Task.Run(() => WriteLabComparison(baseline, lab));
                        notice = "Comparison saved: " + path;
                        await AppendComparisonExplanation(path);
                        OpenFile(path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { Paths.Log("lab-compare", e); notice = "The comparison could not be written (see logs/app.log)."; }
                    RenderBody();
                };
                past.Children.Add(compare);
            }
            stack.Children.Add(past);
        }
        return Card(stack);
    }

    (List<Usage> Usage, List<RequestTrace> Traces, Dictionary<string, MetricPoint?> Peaks) LabData(LabSessions.Lab lab)
    {
        long start = lab.StartMs / 1000, end = (lab.EndMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
        var store = localService.Store;
        var usage = store.UsageSince(start).Where(u => u.Time <= end).ToList();
        var traces = store.Traces(lab.StartMs, lab.EndMs ?? long.MaxValue);
        var peaks = new Dictionary<string, MetricPoint?>();
        foreach (var id in new[] { "gpu.local", "gpu.load", "cpu.load", "ram.used", "gpu.temperature", "gpu.power" })
        {
            var points = store.History(id, start, end);
            peaks[id] = points.Count == 0 ? null : new MetricPoint(end, points.Average(p => p.Average), points.Min(p => p.Minimum), points.Max(p => p.Maximum));
        }
        return (usage, traces, peaks);
    }

    string WriteLabReport(LabSessions.Lab lab)
    {
        long start = lab.StartMs / 1000, end = (lab.EndMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
        var (usage, traces, peaks) = LabData(lab);
        var residency = localService.Residency.Since(start).Where(e => e.Time <= end).ToList();
        var counters = localService.Store.CounterTotals(start, end).Where(c => c.Source == "vLLM counters").ToList();
        string markdown = LabSessions.Build(lab, usage, traces, peaks, residency, counters, Labs.Prices());
        return WriteExport($"lab-{DateTimeOffset.FromUnixTimeMilliseconds(lab.StartMs).ToLocalTime():yyyyMMdd-HHmm}-{Slug(lab.Name)}.md", markdown);
    }

    // Before/after: the earlier lab is the baseline, the later one is the run after a change.
    string WriteLabComparison(LabSessions.Lab before, LabSessions.Lab after)
    {
        var prices = Labs.Prices();
        LabStats Stats(LabSessions.Lab lab) { var (usage, traces, peaks) = LabData(lab); return LabStats.From(lab, usage, traces, peaks, prices); }
        string markdown = LabComparison.Build(Stats(before), Stats(after));
        return WriteExport($"compare-{Slug(after.Name)}-vs-{Slug(before.Name)}-{DateTimeOffset.FromUnixTimeMilliseconds(after.StartMs).ToLocalTime():yyyyMMdd-HHmm}.md", markdown);
    }

    // With the local assistant enabled, a local model adds a plain-language reading of the comparison.
    async Task AppendComparisonExplanation(string path)
    {
        var options = localService.Settings.Assistant;
        if (!options.Enabled) return;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            if (await assistant.ExplainComparison(options, await File.ReadAllTextAsync(path), cancel.Token) is { Length: > 0 } text)
                await File.AppendAllTextAsync(path, Environment.NewLine + "## Explanation (local AI, " + options.Model + ")" + Environment.NewLine + Environment.NewLine + text.Trim() + Environment.NewLine +
                    Environment.NewLine + "Model-written; check it against the table above." + Environment.NewLine);
        }
        catch (Exception e) { Paths.Log("lab-compare-explain", e); notice += " (local AI explanation unavailable)"; }
    }

    static string Slug(string name)
    {
        string slug = new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return slug.Length > 40 ? slug[..40] : slug;
    }

    static string WriteExport(string fileName, string markdown)
    {
        string folder = Path.Combine(Paths.Root, "exports");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName);
        File.WriteAllText(path, markdown);
        return path;
    }

    static void OpenFile(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { OpenFolder(Path.GetDirectoryName(file)!); }
    }
}

// Gantt-style view: each row is an agent run (or an unattributed model), bars are model calls.
internal sealed class AgentTimeline : Control
{
    internal sealed record Row(string Label, long Start, long End, RequestTrace? AgentSpan, List<RequestTrace> Calls);
    public List<RequestTrace> Traces { get; set; } = [];
    public long From { get; set; }
    public long To { get; set; }
    public bool LightTheme { get; set; }
    const double RowHeight = 22, LabelWidth = 190, Axis = 22;
    static readonly string[] Palette = ["#5aa9ff", "#59dfae", "#ff9e64", "#bba3ff", "#ff6b7a", "#e8d45f", "#4fd1d9", "#f78fd2"];

    internal static List<Row> Rows(List<RequestTrace> traces) =>
        traces.GroupBy(t => t.Run != "" ? "run:" + t.Run : t.Agent != "" ? "agent:" + t.Agent : "model:" + t.Model)
            .Select(g =>
            {
                var agentSpan = g.FirstOrDefault(t => t.Outcome == "Agent run");
                string agent = g.Select(t => t.Agent).FirstOrDefault(a => a != "") ?? "";
                string label = agent != "" ? agent + (g.First().Run is { Length: > 0 } run ? " · " + run[..Math.Min(6, run.Length)] : "")
                    : g.Key.StartsWith("model:") ? "unattributed · " + (g.First().Model == "" ? "?" : g.First().Model) : "run " + g.First().Run[..Math.Min(6, g.First().Run.Length)];
                return new Row(label, g.Min(t => t.StartMs), g.Max(t => t.EndMs), agentSpan, g.Where(t => t.Outcome != "Agent run").ToList());
            })
            .OrderByDescending(r => r.End).ToList();

    internal static double HeightFor(List<RequestTrace> traces) => Axis + Math.Min(30, Math.Max(1, Rows(traces).Count)) * RowHeight + 6;

    static Color ModelColor(string model) => Color.Parse(Palette[(int)((uint)model.GetHashCode() % Palette.Length)]);

    public override void Render(DrawingContext ctx)
    {
        var rows = Rows(Traces).Take(30).Reverse().ToList();
        double width = Bounds.Width, plot = Math.Max(10, width - LabelWidth - 8);
        var ink = new SolidColorBrush(Color.Parse(LightTheme ? "#33435c" : "#b3c8e2"));
        var grid = new Pen(new SolidColorBrush(Color.Parse(LightTheme ? "#dfe7f3" : "#22324a")), 1);
        double X(long ms) => LabelWidth + plot * Math.Clamp((ms - From) / (double)Math.Max(1, To - From), 0, 1);
        for (int i = 0; i <= 4; i++)
        {
            long at = From + (To - From) * i / 4; double x = X(at);
            ctx.DrawLine(grid, new Point(x, Axis - 4), new Point(x, Bounds.Height));
            var label = new FormattedText(DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("HH:mm"), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, ink);
            ctx.DrawText(label, new Point(Math.Min(x, width - label.Width), 2));
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; double y = Axis + i * RowHeight;
            var text = new FormattedText(row.Label.Length > 30 ? row.Label[..30] + "…" : row.Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
            ctx.DrawText(text, new Point(4, y + 3));
            if (row.AgentSpan is { } span)
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#bba3ff"), 0.12), new Pen(new SolidColorBrush(Color.Parse("#bba3ff"), 0.7), 1), new Rect(X(span.StartMs), y + 2, Math.Max(2, X(span.EndMs) - X(span.StartMs)), RowHeight - 4), 3, 3);
            foreach (var call in row.Calls)
            {
                var brush = new SolidColorBrush(ModelColor(call.Model), call.HttpStatus >= 400 ? 0.4 : 0.9);
                ctx.DrawRectangle(brush, null, new Rect(X(call.StartMs), y + 5, Math.Max(2, X(call.EndMs) - X(call.StartMs)), RowHeight - 10), 2, 2);
            }
        }
    }
}
