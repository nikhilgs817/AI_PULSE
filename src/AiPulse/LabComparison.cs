using System.Globalization;
using System.Text;

namespace AiPulse;

// Before/after comparison of two lab sessions: the "apply a fix, re-run the lab, see what changed"
// workflow. Pure functions over data the lab report already gathers, so they are testable offline.
public sealed record LabStats(
    string Name, long StartMs, long EndMs, int Requests, int Errors,
    long Input, long Output, long CacheRead, long CacheWrite,
    double? MedianTtft, double? P95Ttft, double? MedianGenRate, double? P95RequestSeconds,
    double? ApiCostUsd, double? PeakVramGiB, double? AverageGpuLoad, double? AverageGpuWatts,
    Dictionary<string, (long Input, long Output)> ByModel)
{
    public double Seconds => Math.Max(0, (EndMs - StartMs) / 1000.0);
    public long Tokens => Input + Output;
    /// <summary>Share of input tokens served from the prompt cache. Null when no input was recorded.</summary>
    public double? CacheHitRatio => Input > 0 ? (double)CacheRead / Input : null;
    public double? ErrorRate => Requests > 0 ? (double)Errors / Requests : null;
    /// <summary>GPU energy per token in joules: average GPU power over the lab times its duration, divided by tokens.</summary>
    public double? JoulesPerToken => AverageGpuWatts is double w && Tokens > 0 && Seconds > 0 ? w * Seconds / Tokens : null;
    public double? TokensPerRequest => Requests > 0 ? (double)Tokens / Requests : null;

    public static LabStats From(LabSessions.Lab lab, List<Usage> usage, List<RequestTrace> traces,
        Dictionary<string, MetricPoint?> peaks, List<LabSessions.Price> prices)
    {
        long end = lab.EndMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var calls = traces.Where(t => t.Outcome != "Agent run").ToList();
        int errors = calls.Count(t => t.HttpStatus >= 400 || t.Outcome.StartsWith("Failed") || t.Outcome.StartsWith("HTTP"));
        var byModel = usage.GroupBy(u => u.Model == "" ? "(unknown)" : u.Model)
            .ToDictionary(g => g.Key, g => (g.Sum(u => u.Input), g.Sum(u => u.Output)));
        return new(lab.Name, lab.StartMs, end, usage.Count, errors,
            usage.Sum(u => u.Input), usage.Sum(u => u.Output), usage.Sum(u => u.Cached), usage.Sum(u => u.CacheWrite),
            LabMath.Percentile(calls.Select(t => t.TtftSeconds), 50), LabMath.Percentile(calls.Select(t => t.TtftSeconds), 95),
            LabMath.Percentile(calls.Select(t => t.GenerateRate), 50),
            LabMath.Percentile(calls.Select(t => (double?)((t.EndMs - t.StartMs) / 1000.0)), 95),
            LabMath.ApiCost(usage, prices),
            peaks.GetValueOrDefault("gpu.local")?.Maximum, peaks.GetValueOrDefault("gpu.load")?.Average, peaks.GetValueOrDefault("gpu.power")?.Average,
            byModel);
    }
}

public static class LabMath
{
    /// <summary>Nearest-rank percentile of the finite values, or null when there are none.</summary>
    public static double? Percentile(IEnumerable<double?> values, double percentile)
    {
        var sorted = values.Where(v => v is double d && double.IsFinite(d)).Select(v => v!.Value).Order().ToArray();
        if (sorted.Length == 0) return null;
        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Finds the price for a model id: exact match first, then the longest configured prefix.</summary>
    public static LabSessions.Price? PriceFor(string model, List<LabSessions.Price> prices) =>
        prices.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase)) ??
        prices.Where(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Model.Length).FirstOrDefault();

    /// <summary>
    /// What the recorded API (tool) usage cost at list prices. Input totals include cache reads and
    /// writes, so those are taken out and priced at their own rates. Null when no record has a price.
    /// </summary>
    public static double? ApiCost(IEnumerable<Usage> usage, List<LabSessions.Price> prices)
    {
        double total = 0; bool any = false;
        foreach (var u in usage)
        {
            if (PriceFor(u.Model, prices) is not { } p) continue;
            any = true;
            long fresh = Math.Max(0, u.Input - u.Cached - u.CacheWrite);
            total += fresh / 1e6 * p.InputPerMillion + u.Output / 1e6 * p.OutputPerMillion +
                     u.Cached / 1e6 * (p.CacheReadPerMillion ?? p.InputPerMillion * 0.1) +
                     u.CacheWrite / 1e6 * (p.CacheWritePerMillion ?? p.InputPerMillion * 1.25);
        }
        return any ? total : null;
    }
}

public static class LabComparison
{
    // Direction tells whether a rise is good (+1), bad (-1) or neutral (0).
    sealed record Row(string Label, double? Before, double? After, string Format, int Direction, string Unit = "");

    public static string Build(LabStats before, LabStats after)
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.AppendLine($"# {after.Name} vs {before.Name}").AppendLine();
        b.AppendLine($"Before: {Stamp(before)} · After: {Stamp(after)}").AppendLine();
        var rows = new List<Row>
        {
            new("Duration", before.Seconds, after.Seconds, "0", 0, " s"),
            new("Requests", before.Requests, after.Requests, "N0", 0),
            new("Input tokens", before.Input, after.Input, "N0", -1),
            new("Output tokens", before.Output, after.Output, "N0", -1),
            new("Tokens per request", before.TokensPerRequest, after.TokensPerRequest, "N0", -1),
            new("Cache hit ratio", Pct(before.CacheHitRatio), Pct(after.CacheHitRatio), "0.0", +1, "%"),
            new("Error rate", Pct(before.ErrorRate), Pct(after.ErrorRate), "0.0", -1, "%"),
            new("Median time to first token", before.MedianTtft, after.MedianTtft, "0.00", -1, " s"),
            new("p95 time to first token", before.P95Ttft, after.P95Ttft, "0.00", -1, " s"),
            new("p95 request time", before.P95RequestSeconds, after.P95RequestSeconds, "0.00", -1, " s"),
            new("Median generation speed", before.MedianGenRate, after.MedianGenRate, "0.0", +1, " tok/s"),
            new("API cost (list prices)", before.ApiCostUsd, after.ApiCostUsd, "0.0000", -1, " USD"),
            new("Peak VRAM", before.PeakVramGiB, after.PeakVramGiB, "0.0", -1, " GiB"),
            new("Average GPU load", before.AverageGpuLoad, after.AverageGpuLoad, "0", 0, "%"),
            new("GPU energy per token", before.JoulesPerToken, after.JoulesPerToken, "0.000", -1, " J"),
        };
        b.AppendLine("| Metric | Before | After | Change | |").AppendLine("|---|---:|---:|---:|---|");
        var better = new List<string>(); var worse = new List<string>();
        foreach (var r in rows)
        {
            string change = "—", verdict = "";
            if (r.Before is double x && r.After is double y)
            {
                double delta = y - x;
                change = (delta >= 0 ? "+" : "") + delta.ToString(r.Format, inv) + r.Unit + (x != 0 && r.Unit != "%" ? $" ({(delta / Math.Abs(x) * 100).ToString("+0;-0;0", inv)}%)" : "");
                // Ignore changes under 2% of the baseline (or under 0.5 points for percentages): run-to-run noise.
                bool material = r.Unit == "%" ? Math.Abs(delta) >= 0.5 : x == 0 ? delta != 0 : Math.Abs(delta / x) >= 0.02;
                if (r.Direction != 0 && material)
                {
                    bool improved = Math.Sign(delta) == r.Direction;
                    verdict = improved ? "better" : "worse";
                    (improved ? better : worse).Add(r.Label.Length > 1 && char.IsLower(r.Label[1]) ? char.ToLowerInvariant(r.Label[0]) + r.Label[1..] : r.Label);
                }
            }
            b.AppendLine($"| {r.Label} | {Value(r.Before, r.Format, r.Unit)} | {Value(r.After, r.Format, r.Unit)} | {change} | {verdict} |");
        }
        b.AppendLine();
        b.AppendLine("## Verdict").AppendLine();
        b.AppendLine(better.Count == 0 && worse.Count == 0 ? "- No material change (all differences under 2%)."
            : (better.Count > 0 ? $"- Better: {string.Join(", ", better)}." : "- Nothing improved materially.") + Environment.NewLine +
              (worse.Count > 0 ? $"- Worse: {string.Join(", ", worse)}." : "- Nothing got materially worse."));
        b.AppendLine();
        b.AppendLine("## Tokens by model").AppendLine();
        b.AppendLine("| Model | Before in / out | After in / out | Change in total |").AppendLine("|---|---:|---:|---:|");
        foreach (var model in before.ByModel.Keys.Union(after.ByModel.Keys).OrderByDescending(m => after.ByModel.GetValueOrDefault(m).Input + after.ByModel.GetValueOrDefault(m).Output))
        {
            var x = before.ByModel.GetValueOrDefault(model); var y = after.ByModel.GetValueOrDefault(model);
            long delta = y.Input + y.Output - x.Input - x.Output;
            b.AppendLine($"| {model.Replace("|", "\\|")} | {x.Input:N0} / {x.Output:N0} | {y.Input:N0} / {y.Output:N0} | {(delta >= 0 ? "+" : "")}{delta:N0} |");
        }
        b.AppendLine().AppendLine("Runs of different length are compared as recorded; per-request rows are the fairer comparison when the workloads differ.");
        return b.ToString();
    }

    static double? Pct(double? ratio) => ratio * 100;
    static string Value(double? v, string format, string unit) => v is double d ? d.ToString(format, CultureInfo.InvariantCulture) + unit : "—";
    static string Stamp(LabStats s) => $"{s.Name}, {DateTimeOffset.FromUnixTimeMilliseconds(s.StartMs).ToLocalTime():MMM d HH:mm} ({TimeSpan.FromSeconds(s.Seconds):h\\:mm\\:ss})";
}
