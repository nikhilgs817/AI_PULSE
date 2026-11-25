using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiPulse;

// "Lab session" recording: Start/Stop around an experiment, then a Markdown report of what ran
// behind the scenes (tokens per model and agent, speed, TTFT, errors, VRAM peaks, model swaps,
// and what the same tokens would have cost on a reference API model).
public sealed class LabSessions
{
    public sealed record Lab(string Id, string Name, long StartMs, long? EndMs, string? Report);
    // Cache rates are optional; when absent, cache reads cost 10% and cache writes 125% of the input rate.
    public sealed record Price(string Model, double InputPerMillion, double OutputPerMillion, double? CacheReadPerMillion = null, double? CacheWritePerMillion = null);

    // Anthropic list prices per 1M tokens (cached 2026-11-03). Edit lab-pricing.json to add or change rates.
    public static readonly Price[] DefaultPrices =
    [
        new("claude-opus-5-5", 4.00, 20.00),
        new("claude-sonnet-5-5", 2.00, 10.00),
        new("claude-haiku-5-5", 0.10, 0.50),
    ];

    static LabSessions? shared;
    /// <summary>The one instance the window and the local lab API share, so their writes never race.</summary>
    public static LabSessions Shared => shared ??= new(Paths.Root);

    readonly string file, pricingFile;
    readonly object gate = new();
    public LabSessions(string root) { file = Path.Combine(root, "labs.json"); pricingFile = Path.Combine(root, "lab-pricing.json"); }

    public List<Lab> All()
    {
        lock (gate)
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<List<Lab>>(File.ReadAllText(file)) ?? [] : []; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Paths.Log("labs", e); return []; }
        }
    }
    public Lab? Current => All().LastOrDefault(l => l.EndMs is null);

    void Save(List<Lab> labs)
    {
        lock (gate)
        {
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(labs.TakeLast(200), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, file, overwrite: true);
        }
    }

    public Lab Start(string name, long nowMs)
    {
        var labs = All();
        if (labs.LastOrDefault(l => l.EndMs is null) is { } open) return open;
        var lab = new Lab(Guid.NewGuid().ToString("N")[..12], string.IsNullOrWhiteSpace(name) ? $"Lab {DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToLocalTime():MMM d HH:mm}" : name.Trim(), nowMs, null, null);
        labs.Add(lab); Save(labs); return lab;
    }

    public Lab? Stop(long nowMs, Func<Lab, string> report)
    {
        var labs = All();
        int index = labs.FindLastIndex(l => l.EndMs is null);
        if (index < 0) return null;
        var lab = labs[index] with { EndMs = nowMs };
        string? path = null;
        try { path = report(lab); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { Paths.Log("lab-report", e); }
        labs[index] = lab with { Report = path };
        Save(labs);
        return labs[index];
    }

    public List<Price> Prices()
    {
        try
        {
            if (!File.Exists(pricingFile)) File.WriteAllText(pricingFile, JsonSerializer.Serialize(DefaultPrices, new JsonSerializerOptions { WriteIndented = true }));
            var prices = JsonSerializer.Deserialize<List<Price>>(File.ReadAllText(pricingFile));
            return prices?.Where(p => p.Model != "" && p.InputPerMillion >= 0 && p.OutputPerMillion >= 0).ToList() is { Count: > 0 } valid ? valid : [.. DefaultPrices];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Paths.Log("lab-pricing", e); return [.. DefaultPrices]; }
    }

    // Pure report builder (testable). Returns Markdown.
    public static string Build(Lab lab, List<Usage> usage, List<RequestTrace> traces, Dictionary<string, MetricPoint?> peaks,
        List<ModelResidency.Event> residency, List<TokenCounterTotal> counters, List<Price> prices)
    {
        var inv = CultureInfo.InvariantCulture;
        long end = lab.EndMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var b = new StringBuilder();
        b.AppendLine($"# {lab.Name}").AppendLine();
        b.AppendLine($"{DateTimeOffset.FromUnixTimeMilliseconds(lab.StartMs).ToLocalTime():yyyy-MM-dd HH:mm:ss} → {DateTimeOffset.FromUnixTimeMilliseconds(end).ToLocalTime():HH:mm:ss zzz} · {TimeSpan.FromMilliseconds(end - lab.StartMs):h\\:mm\\:ss}").AppendLine();
        long input = usage.Sum(u => u.Input), output = usage.Sum(u => u.Output);
        int errors = traces.Count(t => t.HttpStatus >= 400 || t.Outcome.StartsWith("Failed") || t.Outcome.StartsWith("HTTP"));
        b.AppendLine("## Summary").AppendLine();
        b.AppendLine($"- Requests: {usage.Count:N0} recorded, {errors:N0} errors");
        b.AppendLine($"- Tokens: {input:N0} input · {output:N0} output");
        var stats = LabStats.From(lab, usage, traces, peaks, prices);
        if (stats.CacheHitRatio is double hit) b.AppendLine($"- Cache hit ratio: {(hit * 100).ToString("0.0", inv)}% of input tokens read from the prompt cache");
        if (stats.P95Ttft is double p95) b.AppendLine($"- Time to first token: median {Fmt(stats.MedianTtft, "0.00 s")}, p95 {p95.ToString("0.00", inv)} s");
        if (stats.ApiCostUsd is double cost) b.AppendLine($"- API usage cost at list prices: ${cost.ToString("0.0000", inv)} (models priced in lab-pricing.json, cache reads and writes at their own rates)");
        if (stats.JoulesPerToken is double joules) b.AppendLine($"- GPU energy: {joules.ToString("0.000", inv)} J per token (average {stats.AverageGpuWatts!.Value.ToString("0", inv)} W over the run)");
        if (traces.Select(t => t.Agent).Where(a => a != "").Distinct().Count() is int agents and > 0) b.AppendLine($"- Agents: {agents} ({traces.Select(t => t.Run).Where(r => r != "").Distinct().Count()} runs)");
        b.AppendLine();

        b.AppendLine("## What to look at").AppendLine();
        b.Append(LabFindings.Markdown(LabFindings.For(stats, usage, traces, residency.Count(e => e.Kind == "Evicted"), peaks.GetValueOrDefault("gpu.temperature")?.Maximum))).AppendLine();

        b.AppendLine("## Tokens by model").AppendLine();
        b.AppendLine("| Source | Model | Requests | Input | Output | Median TTFT | Prompt tok/s | Gen tok/s |").AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var g in usage.GroupBy(u => (u.Source, u.Model)).OrderByDescending(g => g.Sum(u => u.Input + u.Output)))
        {
            var t = traces.Where(x => x.Model == g.Key.Model).ToList();
            b.AppendLine($"| {g.Key.Source} | {Cell(g.Key.Model)} | {g.Count():N0} | {g.Sum(u => u.Input):N0} | {g.Sum(u => u.Output):N0} | {Fmt(Median(t.Select(x => x.TtftSeconds)), "0.00 s")} | {Fmt(Median(t.Select(x => x.PromptRate)), "0")} | {Fmt(Median(t.Select(x => x.GenerateRate ?? null)), "0.0")} |");
        }
        if (usage.Count == 0) b.AppendLine("| — | No recorded requests | 0 | 0 | 0 | — | — | — |");
        b.AppendLine();

        var byAgent = traces.Where(t => t.Agent != "" || t.Run != "").GroupBy(t => t.Agent == "" ? "(unnamed)" : t.Agent).ToList();
        if (byAgent.Count > 0)
        {
            b.AppendLine("## Agents").AppendLine();
            b.AppendLine("| Agent | Runs | Model calls | Input | Output | Busy time | Errors |").AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var g in byAgent.OrderByDescending(g => g.Sum(t => t.PromptTokens + t.OutputTokens)))
                b.AppendLine($"| {Cell(g.Key)} | {g.Select(t => t.Run).Distinct().Count()} | {g.Count(t => t.Outcome != "Agent run")} | {g.Sum(t => t.PromptTokens):N0} | {g.Sum(t => t.OutputTokens):N0} | {TimeSpan.FromMilliseconds(g.Where(t => t.Outcome != "Agent run").Sum(t => t.EndMs - t.StartMs)):h\\:mm\\:ss} | {g.Count(t => t.HttpStatus >= 400)} |");
            b.AppendLine();
        }

        if (counters.Count > 0)
        {
            b.AppendLine("## vLLM server counters").AppendLine();
            foreach (var g in counters.GroupBy(c => (c.Instance, c.Model)))
                b.AppendLine($"- {g.Key.Instance} · {g.Key.Model}: {g.Where(c => c.Kind == "input").Sum(c => c.Tokens):N0} input, {g.Where(c => c.Kind == "output").Sum(c => c.Tokens):N0} output");
            b.AppendLine();
        }

        b.AppendLine("## Hardware peaks").AppendLine();
        foreach (var (id, label, unit) in new[] { ("gpu.local", "GPU memory (VRAM)", "GiB"), ("gpu.load", "GPU utilization", "%"), ("cpu.load", "CPU utilization", "%"), ("ram.used", "System memory", "GiB"), ("gpu.temperature", "GPU temperature", "°C") })
            b.AppendLine($"- {label}: " + (peaks.GetValueOrDefault(id) is { } p ? $"peak {p.Maximum.ToString("0.#", inv)} {unit}, average {p.Average.ToString("0.#", inv)} {unit}" : "not sampled"));
        var evictions = residency.Count(e => e.Kind == "Evicted");
        b.AppendLine($"- Model loads: {residency.Count(e => e.Kind == "Loaded")}, evictions (VRAM swaps): {evictions}" + (evictions >= ModelResidency.ThrashEvictions ? " ⚠ models competed for VRAM" : ""));
        b.AppendLine();

        b.AppendLine("## If this had run on an API").AppendLine();
        b.AppendLine("Same input and output token counts at list prices (excludes caching and batch discounts; local tokenizers differ).").AppendLine();
        b.AppendLine("| Reference model | Input $/1M | Output $/1M | Estimated cost |").AppendLine("|---|---:|---:|---:|");
        long localIn = usage.Where(u => OnPrem(u.Source)).Sum(u => u.Input) + counters.Where(c => c.Kind == "input").Sum(c => c.Tokens);
        long localOut = usage.Where(u => OnPrem(u.Source)).Sum(u => u.Output) + counters.Where(c => c.Kind == "output").Sum(c => c.Tokens);
        foreach (var p in prices)
            b.AppendLine($"| {Cell(p.Model)} | ${p.InputPerMillion.ToString("0.00", inv)} | ${p.OutputPerMillion.ToString("0.00", inv)} | ${(localIn / 1e6 * p.InputPerMillion + localOut / 1e6 * p.OutputPerMillion).ToString("0.0000", inv)} |");
        b.AppendLine().AppendLine($"Local tokens counted: {localIn:N0} input · {localOut:N0} output. Rates come from lab-pricing.json in the AI Pulse data folder.");
        return b.ToString();
    }

    static bool OnPrem(string source) => source.StartsWith("Ollama") || source.StartsWith("vLLM") || source.StartsWith("Local");
    static string Cell(string s) => s.Replace("|", "\\|");
    static string Fmt(double? v, string format) => v is double d ? d.ToString(format.Split(' ')[0], CultureInfo.InvariantCulture) + (format.Contains(' ') ? " " + format.Split(' ')[1] : "") : "—";
    static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.Where(v => v is double d && double.IsFinite(d)).Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
