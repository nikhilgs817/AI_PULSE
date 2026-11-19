namespace AiPulse;

// Burn-rate projection for subscription windows from successive provider readings.
// A least-squares slope over the last hour, restarted whenever the window resets.
internal sealed class QuotaForecast
{
    internal sealed record Projection(double UsedPercent, double PercentPerHour, DateTimeOffset? LimitAt, double? AtReset, DateTimeOffset? ResetsAt)
    {
        public string Describe(DateTimeOffset now)
        {
            if (PercentPerHour <= 0.05) return $"{UsedPercent:0}% used · not rising";
            if (LimitAt is { } hit && (ResetsAt is null || hit < ResetsAt))
                return hit - now < TimeSpan.FromMinutes(1) ? "at the limit now" : $"at this pace 100% at {hit.ToLocalTime():h:mm tt}" + (ResetsAt is { } r ? $" (resets {r.ToLocalTime():h:mm tt})" : "");
            return AtReset is double p ? $"on pace for ~{Math.Min(100, p):0}% at reset ({PercentPerHour:0.#}%/h)" : $"{PercentPerHour:0.#}%/h";
        }
    }

    sealed record Sample(DateTimeOffset At, double Used, DateTimeOffset? ResetsAt);
    static readonly TimeSpan Lookback = TimeSpan.FromHours(1), MinSpan = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, List<Sample>> series = [];

    public void Add(string key, QuotaWindow? window, DateTimeOffset now)
    {
        if (window is null) return;
        var list = series.TryGetValue(key, out var existing) ? existing : series[key] = [];
        var at = window.ObservedAt ?? now;
        // A different reset time (beyond clock jitter) or a drop in usage means a new window.
        if (list.Count > 0 && (window.UsedPercent + 1 < list[^1].Used || ResetMoved(list[^1].ResetsAt, window.ResetsAt))) list.Clear();
        if (list.Count > 0 && at <= list[^1].At) return;
        list.Add(new(at, window.UsedPercent, window.ResetsAt));
        list.RemoveAll(s => s.At < now - Lookback - Lookback);
    }

    static bool ResetMoved(DateTimeOffset? a, DateTimeOffset? b) => a is { } x && b is { } y && (x - y).Duration() > TimeSpan.FromMinutes(5);

    public Projection? Project(string key, DateTimeOffset now)
    {
        if (!series.TryGetValue(key, out var all)) return null;
        var points = all.Where(s => s.At >= now - Lookback).ToList();
        if (points.Count < 2 || points[^1].At - points[0].At < MinSpan) return null;
        double t0 = points[0].At.ToUnixTimeSeconds();
        double mx = points.Average(p => p.At.ToUnixTimeSeconds() - t0), my = points.Average(p => p.Used);
        double sxx = points.Sum(p => Math.Pow(p.At.ToUnixTimeSeconds() - t0 - mx, 2)), sxy = points.Sum(p => (p.At.ToUnixTimeSeconds() - t0 - mx) * (p.Used - my));
        double perSecond = sxx > 0 ? Math.Max(0, sxy / sxx) : 0;
        var last = points[^1];
        DateTimeOffset? limit = perSecond > 0 ? last.At.AddSeconds((100 - last.Used) / perSecond) : null;
        double? atReset = last.ResetsAt is { } reset ? last.Used + perSecond * Math.Max(0, (reset - last.At).TotalSeconds) : null;
        return new(last.Used, perSecond * 3600, limit, atReset, last.ResetsAt);
    }
}
