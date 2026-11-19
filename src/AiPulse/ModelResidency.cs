namespace AiPulse;

// Tracks which models are resident between polls to expose VRAM pressure:
// - Evicted: a model unloaded in the same poll another model loaded (the server made room).
//   Idle keep-alive expiry unloads alone and is not counted.
// - CPU offload: the model does not fit in VRAM and part of it runs on the CPU (much slower).
public sealed class ModelResidency
{
    public sealed record Event(long Time, string Host, string Model, string Kind, double? SizeGiB, double? GpuGiB);
    public const int ThrashEvictions = 3;
    public const long ThrashWindow = 1800;
    readonly Dictionary<string, ModelInfo> resident = [];
    readonly List<Event> events = [];
    readonly object gate = new();
    bool primed;
    readonly Dictionary<string, (bool Active, string Detail)> raised = [];

    // Writes only on change; an alert that never fired is not stored.
    void Raise(Storage db, string key, string title, string detail, bool active)
    {
        if (raised.TryGetValue(key, out var previous) ? previous == (active, detail) : !active) { raised[key] = (active, detail); return; }
        raised[key] = (active, detail);
        db.Alert(key, title, detail, active);
    }

    static string Key(ModelInfo m) => m.Host + "\n" + m.Name;

    public void Observe(IEnumerable<ModelInfo> models, IReadOnlyDictionary<string, string> status, long now)
    {
        lock (gate)
        {
            // Only compare hosts that answered this poll; an offline host is not an unload.
            var connected = status.Where(s => s.Value == "Connected").Select(s => s.Key).ToHashSet();
            var current = models.Where(m => connected.Contains(m.Host)).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            if (!primed) { foreach (var item in current) resident[item.Key] = item.Value; primed = true; return; }
            var loaded = current.Where(c => !resident.ContainsKey(c.Key)).Select(c => c.Value).ToList();
            var unloaded = resident.Where(r => connected.Contains(r.Value.Host) && !current.ContainsKey(r.Key)).Select(r => r.Value).ToList();
            foreach (var m in unloaded)
            {
                bool evicted = loaded.Any(l => l.Host == m.Host);
                events.Add(new(now, m.Host, m.Name, evicted ? "Evicted" : "Unloaded", m.MemoryGiB, m.GpuGiB));
                resident.Remove(Key(m));
            }
            foreach (var m in loaded) { events.Add(new(now, m.Host, m.Name, "Loaded", m.MemoryGiB, m.GpuGiB)); resident[Key(m)] = m; }
            foreach (var m in current) resident[m.Key] = m.Value;
            events.RemoveAll(e => e.Time < now - 86400);
        }
    }

    public List<Event> Since(long since) { lock (gate) return events.Where(e => e.Time >= since).ToList(); }

    public static double? CpuShare(ModelInfo m) =>
        m.MemoryGiB is > 0 && m.GpuGiB is double gpu && gpu < m.MemoryGiB.Value * 0.95 ? 1 - gpu / m.MemoryGiB.Value : null;

    public void Report(Storage db, IEnumerable<ModelInfo> models, long now)
    {
        var evictions = Since(now - ThrashWindow).Where(e => e.Kind == "Evicted").ToList();
        bool thrash = evictions.Count >= ThrashEvictions;
        Raise(db, "model-thrash", "Models are swapping in and out of VRAM",
            thrash ? $"{evictions.Count} evictions in 30 min ({string.Join(", ", evictions.Select(e => e.Model).Distinct().Take(4))}). Agents using different models compete for VRAM; each swap reloads weights. Use one shared model, smaller quantizations, or a lower context size."
                   : "Model residency is stable.", thrash);
        foreach (var m in models.Where(m => m.Host == "This PC"))
        {
            var share = CpuShare(m);
            Raise(db, "cpu-offload:" + m.Name, $"{m.Name} is running partly on the CPU",
                share is double s ? $"{s:P0} of {m.MemoryGiB:0.0} GiB does not fit in VRAM. Generation is much slower; reduce context length or use a smaller quantization." : "Fits in VRAM.",
                share is > 0.05);
        }
        // A model that was unloaded no longer offloads; close its alert.
        var present = models.Where(m => m.Host == "This PC").Select(m => "cpu-offload:" + m.Name).ToHashSet();
        foreach (var stale in raised.Where(r => r.Key.StartsWith("cpu-offload:") && r.Value.Active && !present.Contains(r.Key)).Select(r => r.Key).ToList())
            Raise(db, stale, stale["cpu-offload:".Length..] + " is running partly on the CPU", "Model unloaded.", false);
    }
}
