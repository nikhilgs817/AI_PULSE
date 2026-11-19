using System.Text.Json;

namespace AiPulse;

internal static class MonitoringFeatureTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        long now = J.Now;

        // ---- Model residency / VRAM pressure ----
        var residency = new ModelResidency();
        var up = new Dictionary<string, string> { ["This PC"] = "Connected" };
        ModelInfo M(string name, double size = 8, double gpu = 8) => new("This PC", name, "Loaded", size, gpu, "Q4_K_M", 8192, name);
        residency.Observe([M("a")], up, now);
        residency.Observe([M("b")], up, now + 4);          // a evicted for b
        residency.Observe([], up, now + 8);                  // b idle-unloaded alone
        residency.Observe([M("a")], new Dictionary<string, string> { ["This PC"] = "Offline / unavailable" }, now + 12);
        var events = residency.Since(now);
        check(events.Count(e => e.Kind == "Evicted" && e.Model == "a") == 1 && events.Count(e => e.Kind == "Unloaded" && e.Model == "b") == 1 &&
              events.Count(e => e.Kind == "Loaded") == 1, "Residency separates evictions (VRAM pressure) from idle unloads and ignores offline polls");
        check(ModelResidency.CpuShare(M("x", 10, 6)) is double share && Math.Abs(share - 0.4) < 1e-9 && ModelResidency.CpuShare(M("y", 10, 10)) is null,
            "CPU offload share is the part of the model outside VRAM");

        // ---- Burn-rate forecast ----
        var forecast = new QuotaForecast();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(now);
        var reset = t0.AddHours(3);
        for (int i = 0; i <= 6; i++) forecast.Add("k", new QuotaWindow(40 + i * 2, reset, t0.AddMinutes(i * 5)), t0.AddMinutes(i * 5));
        var p = forecast.Project("k", t0.AddMinutes(30));
        check(p is { } proj && Math.Abs(proj.PercentPerHour - 24) < 0.01 && proj.LimitAt is { } hit && Math.Abs((hit - t0.AddMinutes(30)).TotalMinutes - 120) < 1,
            "Burn rate is projected from successive readings (24%/h from 52% hits 100% in 2 hours)");
        forecast.Add("k", new QuotaWindow(3, reset.AddHours(5), t0.AddMinutes(40)), t0.AddMinutes(40));
        check(forecast.Project("k", t0.AddMinutes(40)) is null, "A window reset restarts the projection instead of mixing windows");

        // ---- vLLM /metrics parsing ----
        const string metrics = """
            # HELP vllm:num_requests_running Number of requests running.
            vllm:num_requests_running{engine="0",model_name="Qwen/Qwen3-8B"} 3.0
            vllm:num_requests_waiting{engine="0",model_name="Qwen/Qwen3-8B"} 1.0
            vllm:kv_cache_usage_perc{engine="0",model_name="Qwen/Qwen3-8B"} 0.42
            vllm:prompt_tokens_total{engine="0",model_name="Qwen/Qwen3-8B"} 1000.0
            vllm:prompt_tokens_total{engine="1",model_name="Qwen/Qwen3-8B"} 500.0
            vllm:time_to_first_token_seconds_sum{engine="0",model_name="Qwen/Qwen3-8B"} 2.5
            process_start_time_seconds 1.7e9
            python_gc_objects_collected_total{generation="0"} 99
            """;
        var parsed = VllmMetrics.Parse(metrics, out var models);
        check(models.SetEquals(["Qwen/Qwen3-8B"]) && parsed["vllm:prompt_tokens_total|Qwen/Qwen3-8B"] == 1500 && parsed["vllm:kv_cache_usage_perc|Qwen/Qwen3-8B"] == 0.42 &&
              parsed["process_start_time_seconds|"] == 1.7e9 && !parsed.Keys.Any(k => k.StartsWith("python_")),
            "vLLM Prometheus text is summed per model across engines and non-vLLM series are ignored");

        // ---- Agent labels and Ollama timings ----
        check(AgentLabel.Clean(" planner-agent ") == "planner-agent" && AgentLabel.Clean("bad\nname") == "" && AgentLabel.Clean(new string('x', 81)) == "" && AgentLabel.Clean("Phoenix/researcher#2") == "Phoenix/researcher#2",
            "Agent header values are trimmed and restricted to short printable identifiers");
        var observer = new InferenceUsageObserver();
        observer.Feed(System.Text.Encoding.UTF8.GetBytes("""{"model":"llama3","done":true,"prompt_eval_count":200,"eval_count":50,"load_duration":1500000000,"prompt_eval_duration":400000000,"eval_duration":2000000000}""" + "\n"));
        observer.Finish();
        check(observer.LoadSeconds == 1.5 && observer.PromptSeconds == 0.4 && observer.GenerateSeconds == 2 && observer.Rate == 25,
            "Ollama load, prompt-processing and generation durations are captured separately");

        // ---- Trace storage, retention guard and replication ----
        using (var db = new Storage(Path.Combine(root, "traces.db"), 30, replication: true))
        {
            long ms = now * 1000;
            var trace = new RequestTrace("ollama:t1", "Ollama", "llama3", "planner", "run-1", ms - 3000, ms, 0.6, 0.4, 2, 1.5, 200, 50, 200, "Completed");
            db.PutTrace(trace);
            db.PutTrace(trace with { Id = "ollama:old", StartMs = (now - 40 * 86400) * 1000, EndMs = (now - 40 * 86400) * 1000 + 10 });
            var stored = db.Traces(ms - 10_000);
            check(stored.Count == 1 && stored[0] == trace && stored[0].PromptRate == 500 && stored[0].GenerateRate == 25,
                "Request traces round-trip with derived prompt and generation rates; records outside retention are refused");
            var events2 = db.Events().Where(e => e.Kind == "request-trace").ToList();
            using var replica = new Storage(Path.Combine(root, "traces-replica.db"), 30, replication: true);
            replica.ApplyBatch("test-stream", events2, false);
            check(events2.Count == 1 && replica.Traces(ms - 10_000).SingleOrDefault() == trace, "Request traces replicate from the relay to the dashboard");
            bool rejected = false;
            try { Storage.ValidateTrace(trace with { Agent = "x\u0001" }); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Replicated traces with control characters are rejected");
        }

        // ---- Health classification ----
        check(MainWindow.Classify(new("Relay", "Disconnected", "", now, 0), now) == MainWindow.Health.Broken &&
              MainWindow.Classify(new("Codex", "Receiving", "", now - 300, now), now) == MainWindow.Health.Stale &&
              MainWindow.Classify(new("Ollama gateway", "Ready", "", now - 3600, 0), now) == MainWindow.Health.Waiting &&
              MainWindow.Classify(new("Codex", "Receiving", "", now, now), now) == MainWindow.Health.Receiving &&
              MainWindow.Classify(new("Cowork", "Needs exporter", "", now, 0), now) == MainWindow.Health.Unavailable,
            "Coverage health maps states to Receiving, Waiting, Stale, Broken and Not available; quiet event-driven sources are not stale");

        // ---- Lab report ----
        var lab = new LabSessions.Lab("id", "Phoenix run", (now - 600) * 1000, now * 1000, null);
        var usage = new List<Usage> { new("u1", "Ollama", "llama3", "", now - 300, 1_000_000, 500_000, 0, 0, 0, 3000, 25, "Completed") };
        var traces = new List<RequestTrace> { new("u1", "Ollama", "llama3", "planner", "r1", (now - 303) * 1000, (now - 300) * 1000, 0.5, 0.4, 2, null, 1_000_000, 500_000, 200, "Completed") };
        var peaks = new Dictionary<string, MetricPoint?> { ["gpu.local"] = new(now, 6, 2, 7.5) };
        string report = LabSessions.Build(lab, usage, traces, peaks, [], [], [new("claude-sonnet-5-5", 2, 10)]);
        check(report.Contains("# Phoenix run") && report.Contains("| Ollama | llama3 | 1 | 1,000,000 | 500,000 | 0.50 s |") && report.Contains("| planner | 1 | 1 |") &&
              report.Contains("peak 7.5 GiB") && report.Contains("| claude-sonnet-5-5 | $2.00 | $10.00 | $7.0000 |"),
            "Lab report lists tokens per model and agent, TTFT, VRAM peak and the equivalent API cost");
        var labs = new LabSessions(root);
        var started = labs.Start("t", 1000);
        check(labs.Start("other", 2000).Id == started.Id && labs.Stop(3000, _ => "r.md") is { EndMs: 3000, Report: "r.md" } && labs.Current is null,
            "Only one lab records at a time and stopping stores its report path");
        check(labs.Prices().Any(p => p.Model == "claude-opus-5-5") && File.Exists(Path.Combine(root, "lab-pricing.json")), "Editable lab pricing defaults are written on first use");
    }
}
