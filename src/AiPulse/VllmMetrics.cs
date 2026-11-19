using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiPulse;

// Direct reader for vLLM's built-in Prometheus endpoint (no Collector needed). It exposes what a
// vLLM operator watches: queue depth, KV-cache pressure, preemptions, TTFT and token throughput.
// Token counters also feed the "vLLM counters" ledger so daily totals work without OTLP. Use one
// path per server: do not also export the same server's counters through an OTLP Collector.
public sealed partial class VllmMetrics : IDisposable
{
    public sealed record Server(string Url, string Model, bool Online, double Running, double Waiting, double? KvCache,
        double Preemptions, double? PromptTps, double? GenerateTps, double? TtftSeconds, double? LatencySeconds, long? MaxModelLen, string Error);

    readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    readonly Dictionary<string, (long At, Dictionary<string, double> Values)> previous = [];
    readonly Dictionary<string, long?> contextLengths = [];
    readonly Dictionary<string, (bool Active, string Detail)> raised = [];
    readonly Dictionary<string, (long Next, List<Server> Last)> schedule = [];
    public List<Server> Servers { get; private set; } = [];

    [GeneratedRegex(@"^([a-zA-Z_:][a-zA-Z0-9_:]*)(\{[^}]*\})?\s+([-+0-9.eEinfINFNa]+)")]
    private static partial Regex Line();
    [GeneratedRegex(@"model_name=""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ModelLabel();

    // Sums samples per (metric, model) across other labels such as engine or finished_reason.
    internal static Dictionary<string, double> Parse(string text, out HashSet<string> models)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        models = [];
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            var m = Line().Match(raw.TrimEnd('\r'));
            if (!m.Success || !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) continue;
            string name = m.Groups[1].Value;
            if (!name.StartsWith("vllm:", StringComparison.Ordinal) && name != "process_start_time_seconds") continue;
            string model = ModelLabel().Match(m.Groups[2].Value) is { Success: true } label ? label.Groups[1].Value : "";
            if (model != "") models.Add(model);
            string key = name + "|" + model;
            values[key] = values.GetValueOrDefault(key) + value;
        }
        return values;
    }

    static double Get(Dictionary<string, double> v, string model, params string[] names)
    {
        foreach (var name in names) if (v.TryGetValue(name + "|" + model, out double value)) return value;
        return double.NaN;
    }

    public async Task Poll(IEnumerable<string> urls, Storage db, CancellationToken ct)
    {
        var servers = new List<Server>();
        long now = J.Now;
        foreach (var raw in urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(16))
        {
            string url;
            try { url = Ollama.Validate(raw); } catch (InvalidOperationException) { continue; }
            // Offline or non-vLLM addresses are re-checked once a minute, live servers every poll.
            if (schedule.TryGetValue(url, out var planned) && now < planned.Next) { servers.AddRange(planned.Last); continue; }
            int before = servers.Count;
            try
            {
                string text = await client.GetStringAsync(url + "/metrics", ct);
                var values = Parse(text, out var models);
                if (models.Count == 0) { servers.Add(new(url, "", false, 0, 0, null, 0, null, null, null, null, null, "No vLLM metrics at this address.")); continue; }
                if (!contextLengths.ContainsKey(url)) contextLengths[url] = await MaxModelLen(url, ct);
                previous.TryGetValue(url, out var last);
                double seconds = last.Values != null ? now - last.At : 0;
                double? Rate(string model, string name)
                {
                    double current = Get(values, model, name), before = last.Values != null ? Get(last.Values, model, name) : double.NaN;
                    return seconds > 0 && !double.IsNaN(current) && !double.IsNaN(before) && current >= before ? (current - before) / seconds : null;
                }
                double? Mean(string model, string histogram)
                {
                    double sum = Get(values, model, histogram + "_sum"), count = Get(values, model, histogram + "_count");
                    double sum0 = last.Values != null ? Get(last.Values, model, histogram + "_sum") : double.NaN, count0 = last.Values != null ? Get(last.Values, model, histogram + "_count") : double.NaN;
                    return !double.IsNaN(sum0) && count - count0 > 0 && sum >= sum0 ? (sum - sum0) / (count - count0) : null;
                }
                double started = Get(values, "", "process_start_time_seconds");
                string instance = new Uri(url).Authority;
                foreach (var model in models)
                {
                    double kv = Get(values, model, "vllm:kv_cache_usage_perc", "vllm:gpu_cache_usage_perc");
                    double preempt0 = last.Values != null ? Get(last.Values, model, "vllm:num_preemptions_total") : double.NaN, preempt = Get(values, model, "vllm:num_preemptions_total");
                    var server = new Server(url, model, true, Zero(Get(values, model, "vllm:num_requests_running")), Zero(Get(values, model, "vllm:num_requests_waiting")),
                        double.IsNaN(kv) ? null : kv, double.IsNaN(preempt0) || double.IsNaN(preempt) || preempt < preempt0 ? 0 : preempt - preempt0,
                        Rate(model, "vllm:prompt_tokens_total"), Rate(model, "vllm:generation_tokens_total"),
                        Mean(model, "vllm:time_to_first_token_seconds"), Mean(model, "vllm:e2e_request_latency_seconds"), contextLengths[url], "");
                    servers.Add(server);
                    // Cumulative token counters into the ledger; the process start time detects restarts.
                    if (!double.IsNaN(started) && started > 0)
                        foreach (var (metric, kind) in new[] { ("vllm:prompt_tokens_total", "input"), ("vllm:generation_tokens_total", "output") })
                        {
                            double total = Get(values, model, metric);
                            if (double.IsNaN(total) || total < 0) continue;
                            db.ObserveCounter("vllm-direct:" + J.Hash(url + "\n" + model + "\n" + kind), "vLLM counters", instance, model, kind, (long)started, now, (long)total, true);
                        }
                    Alerts(db, server);
                }
                previous[url] = (now, values);
                db.SourceState("vLLM /metrics · " + instance, "Receiving", $"{models.Count} model(s) served. Queue, KV cache, TTFT and token counters read directly from vLLM.", now);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
            {
                if (ct.IsCancellationRequested) throw;
                servers.Add(new(url, "", false, 0, 0, null, 0, null, null, null, null, null, "Offline / unavailable"));
            }
            var polled = servers.Skip(before).ToList();
            schedule[url] = (polled.Any(p => p.Online) ? 0 : now + 60, polled);
        }
        Servers = servers;
    }

    static double Zero(double v) => double.IsNaN(v) ? 0 : v;

    async Task<long?> MaxModelLen(string url, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync(url + "/v1/models", ct));
            foreach (var m in doc.RootElement.Get("data").EnumerateArray())
                if (m.Get("max_model_len") is { ValueKind: JsonValueKind.Number } len && len.TryGetInt64(out long n)) return n;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { if (ct.IsCancellationRequested) throw; }
        return null;
    }

    void Alerts(Storage db, Server s)
    {
        void Raise(string key, string title, string detail, bool active)
        {
            if (raised.TryGetValue(key, out var previous) ? previous == (active, detail) : !active) { raised[key] = (active, detail); return; }
            raised[key] = (active, detail); db.Alert(key, title, detail, active);
        }
        string id = new Uri(s.Url).Authority + " · " + s.Model;
        Raise("vllm-kv:" + id, "vLLM KV cache nearly full", s.KvCache is double kv ? $"{id}: KV cache {kv:P0} used. New requests will queue or preempt running ones; lower max_num_seqs or context, or add GPU memory." : "", s.KvCache is >= 0.9);
        Raise("vllm-preempt:" + id, "vLLM is preempting requests", $"{id}: {s.Preemptions:N0} preemptions since the last poll. Requests are being paused and recomputed because KV cache ran out.", s.Preemptions > 0);
        Raise("vllm-queue:" + id, "vLLM requests are queueing", $"{id}: {s.Waiting:N0} waiting, {s.Running:N0} running.", s.Waiting >= 4);
    }

    public void Dispose() => client.Dispose();
}
