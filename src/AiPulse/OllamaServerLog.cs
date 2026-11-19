using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiPulse;

// Zero-configuration backstop for Ollama coverage. Ollama's request log ([GIN] lines) records every
// HTTP request with status and latency, but no tokens or content. AI Pulse reads only those lines to
// (1) show request activity even when no capture route is active and (2) count calls that bypassed
// the relay/gateway, so a coverage gap is visible instead of silently reading as "no usage".
public sealed partial class OllamaServerLog
{
    public sealed record Request(long Time, int Status, double Seconds, string Method, string Path);
    static readonly string[] Inference = ["/api/chat", "/api/generate", "/api/embed", "/api/embeddings", "/v1/chat/completions", "/v1/completions", "/v1/embeddings", "/v1/responses", "/v1/messages"];
    const long InitialTail = 512 * 1024, MaxRead = 4 * 1024 * 1024;
    const int MaxKept = 20000;
    readonly Dictionary<string, long> offsets = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Request> requests = [];
    readonly object gate = new();
    readonly Func<IEnumerable<string>> files;

    public OllamaServerLog(Func<IEnumerable<string>>? files = null) => this.files = files ?? DefaultFiles;

    public static IEnumerable<string> DefaultFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local != "") yield return Path.Combine(local, "Ollama", "server.log");
        if (Paths.Root != "") yield return RelayBackendLog;
    }
    public static string RelayBackendLog => Path.Combine(Paths.Root, "relay", "ollama-backend.log");

    [GeneratedRegex(@"^\[GIN\] (\d{4}/\d{2}/\d{2} - \d{2}:\d{2}:\d{2}) \|\s*(\d{3}) \|\s*([^|]+?)\s*\|\s*[^|]*\|\s*([A-Z]+)\s+""([^""]{1,256})""")]
    private static partial Regex Gin();

    public void Poll()
    {
        foreach (var file in files())
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                long length = info.Length;
                long offset = offsets.TryGetValue(file, out var known) ? known : Math.Max(0, length - InitialTail);
                if (length < offset) offset = 0; // rotated (server.log -> server-1.log)
                if (length == offset) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Position = offset;
                var buffer = new byte[(int)Math.Min(MaxRead, length - offset)];
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                int end = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
                if (end < 0) { if (read == MaxRead) offsets[file] = offset + read; continue; }
                var parsed = Parse(Encoding.UTF8.GetString(buffer, 0, end + 1));
                lock (gate)
                {
                    requests.AddRange(parsed);
                    if (requests.Count > MaxKept) requests.RemoveRange(0, requests.Count - MaxKept);
                }
                offsets[file] = offset + end + 1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Paths.Log("ollama-server-log", e); }
        }
    }

    internal static List<Request> Parse(string text)
    {
        var results = new List<Request>();
        foreach (var raw in text.Split('\n'))
        {
            var match = Gin().Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;
            if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy/MM/dd - HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) continue;
            results.Add(new(new DateTimeOffset(local).ToUnixTimeSeconds(), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                GoDuration(match.Groups[3].Value) ?? 0, match.Groups[4].Value, match.Groups[5].Value.Split('?')[0]));
        }
        return results;
    }

    // Go's time.Duration.String(): "0s", "512.3µs", "45.1ms", "2.5s", "1m2.5s", "1h0m3s".
    internal static double? GoDuration(string text)
    {
        text = text.Trim().Replace("µ", "u").Replace("μ", "u");
        if (text.Length == 0) return null;
        double total = 0; int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
            if (start == i || !double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
            int unitStart = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            total += text[unitStart..i] switch { "h" => value * 3600, "m" => value * 60, "s" => value, "ms" => value / 1e3, "us" => value / 1e6, "ns" => value / 1e9, _ => double.NaN };
            if (double.IsNaN(total)) return null;
        }
        return total;
    }

    public static bool IsInference(Request r) => r.Method == "POST" && Inference.Contains(r.Path, StringComparer.Ordinal);

    public List<Request> Since(long since) { lock (gate) return requests.Where(r => r.Time >= since).ToList(); }

    // Compares logged inference calls with captured usage records in a settled window (the relay
    // syncs every few seconds, so the newest two minutes are excluded).
    public (int Logged, int Captured) Coverage(Storage db, long now)
    {
        long from = now - 3600, until = now - 120;
        int logged = Since(from).Count(r => r.Time <= until && IsInference(r));
        int captured = db.UsageSince(from, "Ollama").Count(u => u.Time <= until + 60);
        return (logged, captured);
    }

    public void Report(Storage db, long now)
    {
        var recent = Since(now - 3600).Where(IsInference).ToList();
        if (recent.Count == 0)
        {
            db.SourceState("Ollama request log", "Idle", "No Ollama inference requests in the server log during the last hour.");
            return;
        }
        int errors = recent.Count(r => r.Status >= 400);
        db.SourceState("Ollama request log", "Receiving", $"{recent.Count} inference requests in the last hour, {errors} errors, median {Median(recent.Select(r => r.Seconds)):0.0} s. Counts only; tokens come from capture.", recent.Max(r => r.Time));
        var (logged, captured) = Coverage(db, now);
        int missing = Math.Max(0, logged - captured);
        bool gap = logged >= 3 && missing > Math.Max(2, logged / 10);
        db.SourceState("Ollama coverage", gap ? "Gaps" : "Covered", gap
            ? $"{missing} of {logged} Ollama requests in the last hour were not captured (tokens unknown). Clients may be calling the private backend port, or capture was off."
            : $"{captured} of {logged} logged Ollama requests in the last hour were captured.");
        db.Alert("ollama-coverage", "Ollama traffic not captured", $"{missing} of {logged} requests in the last hour bypassed token capture.", gap);
    }

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
