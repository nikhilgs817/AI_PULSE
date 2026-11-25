using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiPulse;

// Model bench: a load sweep (how many requests at once a local model server handles before it slows down)
// and small evals (score a model's answers, so a change can be judged on quality as well as tokens).

public sealed record SweepPoint(int Concurrency, int Requests, int Errors, double P50Ms, double P95Ms, double TokensPerSecond, double RequestsPerSecond, string? FirstError = null);
public sealed record SweepResult(string Model, DateTimeOffset Time, List<SweepPoint> Points, int? Knee);

public static class LoadSweep
{
    public static readonly int[] Levels = [1, 2, 4, 8];
    public const string Prompt = "Write three short sentences about why the sky looks blue.";

    /// <summary>Sends <paramref name="perLevel"/> requests at each concurrency level and measures latency and throughput.</summary>
    public static async Task<SweepResult> Run(HttpClient http, string baseUrl, string model, IReadOnlyList<int> levels, int perLevel, int maxTokens, Action<string> progress, CancellationToken cancel)
    {
        var points = new List<SweepPoint>();
        // One warm-up call so the first level does not pay for loading the model.
        progress("Loading the model");
        try { await Generate(http, baseUrl, model, "Say ready.", 4, cancel); } catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException) { }
        foreach (int level in levels)
        {
            progress($"{level} request{(level == 1 ? "" : "s")} at a time");
            var latencies = new ConcurrentBag<double>();
            long tokens = 0; int errors = 0, next = 0; string? firstError = null;
            var clock = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, level).Select(async _ =>
            {
                while (Interlocked.Increment(ref next) <= perLevel)
                {
                    var one = Stopwatch.StartNew();
                    try { var (_, n) = await Generate(http, baseUrl, model, Prompt, maxTokens, cancel); Interlocked.Add(ref tokens, n); latencies.Add(one.Elapsed.TotalMilliseconds); }
                    catch (Exception e) when (!cancel.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { Interlocked.Increment(ref errors); Interlocked.CompareExchange(ref firstError, e.Message, null); }
                }
            }));
            double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            var sorted = latencies.Order().ToList();
            points.Add(new(level, perLevel, errors, Percentile(sorted, 50), Percentile(sorted, 95), tokens / seconds, (perLevel - errors) / seconds, firstError));
            if (errors == perLevel) break;
        }
        return new(model, DateTimeOffset.Now, points, Knee(points));
    }

    public static async Task<(string Text, long Tokens)> Generate(HttpClient http, string baseUrl, string model, string prompt, int maxTokens, CancellationToken cancel)
    {
        var body = new { model, prompt, stream = false, options = new { num_predict = maxTokens, temperature = 0 } };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/generate") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(Gateway.AgentHeader, "AI Pulse bench");
        using var response = await http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode} from {model}: {Trim(text, 160)}");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string answer = root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
        long count = root.TryGetProperty("eval_count", out var c) && c.TryGetInt64(out long v) ? v : 0;
        return (answer, count);
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double rank = p / 100 * (sorted.Count - 1);
        int low = (int)Math.Floor(rank), high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    /// <summary>
    /// The concurrency to run at: the last level before throughput stops growing by at least 10%,
    /// errors appear, or the 95th-percentile latency more than doubles from one request at a time.
    /// Null when throughput was still climbing at the highest level tried.
    /// </summary>
    public static int? Knee(IReadOnlyList<SweepPoint> points)
    {
        if (points.Count == 0) return null;
        if (points[0].Errors > 0) return null;
        for (int i = 1; i < points.Count; i++)
            if (points[i].Errors > 0 || points[i].TokensPerSecond < points[i - 1].TokensPerSecond * 1.10 || points[i].P95Ms > points[0].P95Ms * 2)
                return points[i - 1].Concurrency;
        return null;
    }

    public static string Markdown(SweepResult result)
    {
        var b = new StringBuilder();
        b.AppendLine($"# Load sweep: {result.Model}").AppendLine();
        b.AppendLine($"{result.Time:yyyy-MM-dd HH:mm} · {result.Points.FirstOrDefault()?.Requests ?? 0} requests per level · prompt: “{Prompt}”").AppendLine();
        b.AppendLine(Verdict(result)).AppendLine();
        b.AppendLine("| At once | Requests/s | Output tokens/s | p50 | p95 | Errors |").AppendLine("|---:|---:|---:|---:|---:|---:|");
        foreach (var p in result.Points) b.AppendLine($"| {p.Concurrency} | {p.RequestsPerSecond:0.00} | {p.TokensPerSecond:0.0} | {p.P50Ms / 1000:0.00} s | {p.P95Ms / 1000:0.00} s | {p.Errors} |");
        foreach (var p in result.Points.Where(p => p.FirstError != null)) b.AppendLine().Append($"First error at {p.Concurrency} at a time: {p.FirstError}");
        b.AppendLine().AppendLine("Ollama serves several requests at once only up to OLLAMA_NUM_PARALLEL; beyond that they queue, which shows here as latency rising with no gain in tokens/s.");
        return b.ToString();
    }

    public static string Verdict(SweepResult result) =>
        result.Points.Count == 0 ? "No results." :
        result.Points.All(p => p.Errors == p.Requests) ? "Every request failed. Check that the model name is right and Ollama is running." :
        result.Knee is int k ? $"Best at {k} request{(k == 1 ? "" : "s")} at a time: past that, throughput stops growing and requests wait longer." :
        $"Still scaling at {result.Points[^1].Concurrency} at a time. Try more parallel requests in your lab.";
}

public sealed record EvalCase(string Id, string Prompt, string? Contains, string? Regex, string? Not);
public sealed record EvalAnswer(string Id, bool Passed, string Why, string Answer, long Tokens, double Ms);
public sealed record EvalRun(string Suite, string Model, long Time, List<EvalAnswer> Answers)
{
    public int Passed => Answers.Count(a => a.Passed);
    public double PassRate => Answers.Count == 0 ? 0 : (double)Passed / Answers.Count;
    public long Tokens => Answers.Sum(a => a.Tokens);
    public double MeanMs => Answers.Count == 0 ? 0 : Answers.Average(a => a.Ms);
}

public static class Evals
{
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static string Folder => Path.Combine(Paths.Root, "evals");
    static string ResultsFolder => Path.Combine(Folder, "results");

    /// <summary>One case per line: {"id":"…","prompt":"…","contains":"…","regex":"…","not":"…"}. Blank lines and lines starting with # are skipped.</summary>
    public static List<EvalCase> Parse(string jsonl)
    {
        var cases = new List<EvalCase>();
        int line = 0;
        foreach (var raw in jsonl.Split('\n'))
        {
            line++;
            string text = raw.Trim();
            if (text == "" || text.StartsWith('#')) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var e = doc.RootElement;
                string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string prompt = Str("prompt") ?? throw new FormatException($"Line {line}: \"prompt\" is missing.");
                string? regex = Str("regex");
                if (regex != null) _ = new Regex(regex);
                cases.Add(new(Str("id") ?? $"case-{cases.Count + 1}", prompt, Str("contains"), regex, Str("not")));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new FormatException($"Line {line}: {ex.Message}"); }
        }
        return cases;
    }

    /// <summary>Every check the case names must hold; a case with no checks passes on any non-empty answer.</summary>
    public static (bool Passed, string Why) Score(EvalCase c, string answer)
    {
        if (c.Contains is { } want && !answer.Contains(want, StringComparison.OrdinalIgnoreCase)) return (false, $"missing “{want}”");
        if (c.Regex is { } pattern)
        {
            try { if (!Regex.IsMatch(answer, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) return (false, "pattern not matched"); }
            catch (RegexMatchTimeoutException) { return (false, "pattern timed out"); }
        }
        if (c.Not is { } banned && answer.Contains(banned, StringComparison.OrdinalIgnoreCase)) return (false, $"contains “{banned}”");
        if (c.Contains == null && c.Regex == null && c.Not == null && answer.Trim() == "") return (false, "empty answer");
        return (true, "");
    }

    public static async Task<EvalRun> Run(HttpClient http, string baseUrl, string model, string suite, IReadOnlyList<EvalCase> cases, int maxTokens, Action<string> progress, CancellationToken cancel)
    {
        var answers = new List<EvalAnswer>();
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            progress($"Case {i + 1} of {cases.Count}");
            var clock = Stopwatch.StartNew();
            try
            {
                var (text, tokens) = await LoadSweep.Generate(http, baseUrl, model, c.Prompt, maxTokens, cancel);
                var (passed, why) = Score(c, text);
                answers.Add(new(c.Id, passed, why, text.Length > 2000 ? text[..2000] : text, tokens, clock.Elapsed.TotalMilliseconds));
            }
            catch (Exception e) when (!cancel.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            { answers.Add(new(c.Id, false, "request failed: " + e.Message, "", 0, clock.Elapsed.TotalMilliseconds)); }
        }
        return new(suite, model, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), answers);
    }

    public static string Save(EvalRun run)
    {
        Directory.CreateDirectory(ResultsFolder);
        string path = Path.Combine(ResultsFolder, $"{Safe(run.Suite)}--{Safe(run.Model)}--{run.Time}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run, Options));
        return path;
    }

    static string Safe(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray());

    /// <summary>The run of the same suite and model just before <paramref name="run"/>, if any.</summary>
    public static EvalRun? Previous(EvalRun run)
    {
        if (!Directory.Exists(ResultsFolder)) return null;
        EvalRun? best = null;
        foreach (var file in Directory.EnumerateFiles(ResultsFolder, $"{Safe(run.Suite)}--{Safe(run.Model)}--*.json"))
        {
            try
            {
                var other = JsonSerializer.Deserialize<EvalRun>(File.ReadAllText(file), Options);
                if (other != null && other.Time < run.Time && (best == null || other.Time > best.Time)) best = other;
            }
            catch (Exception e) when (e is JsonException or IOException) { Paths.Log("evals", e); }
        }
        return best;
    }

    public static IEnumerable<string> Suites() =>
        Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f)).Order() : [];

    public static void EnsureSample()
    {
        Directory.CreateDirectory(Folder);
        if (Directory.EnumerateFiles(Folder, "*.jsonl").Any()) return;
        File.WriteAllText(Path.Combine(Folder, "sample.jsonl"), """
            # One case per line. "contains", "regex" and "not" are checks on the answer; all that are given must hold.
            {"id":"arithmetic","prompt":"What is 17 * 23? Answer with the number only.","contains":"391"}
            {"id":"capital","prompt":"What is the capital of Australia? Answer in one word.","contains":"Canberra"}
            {"id":"json","prompt":"Reply with only a JSON object whose key ok is true.","regex":"\"ok\"\\s*:\\s*true"}
            {"id":"no-apology","prompt":"Name one primary colour.","regex":"red|blue|yellow","not":"sorry"}
            {"id":"date-format","prompt":"Write 5 March 2026 in ISO 8601 format. Answer with the date only.","contains":"2026-03-05"}

            """);
    }

    public static string Markdown(EvalRun run, EvalRun? before)
    {
        var b = new StringBuilder();
        b.AppendLine($"# Evals: {run.Suite} on {run.Model}").AppendLine();
        b.AppendLine($"{DateTimeOffset.FromUnixTimeMilliseconds(run.Time).ToLocalTime():yyyy-MM-dd HH:mm} · {run.Passed} of {run.Answers.Count} passed ({run.PassRate:P0})" +
            (before == null ? "" : $" · before: {before.Passed} of {before.Answers.Count} ({before.PassRate:P0}) on {DateTimeOffset.FromUnixTimeMilliseconds(before.Time).ToLocalTime():MMM d HH:mm}")).AppendLine();
        if (before != null)
        {
            b.AppendLine("| | Before | After | Change |").AppendLine("|---|---:|---:|---:|");
            b.AppendLine($"| Pass rate | {before.PassRate:P0} | {run.PassRate:P0} | {(run.PassRate - before.PassRate) * 100:+0;-0;0} pts |");
            b.AppendLine($"| Output tokens | {before.Tokens:N0} | {run.Tokens:N0} | {Change(before.Tokens, run.Tokens)} |");
            b.AppendLine($"| Mean latency | {before.MeanMs / 1000:0.00} s | {run.MeanMs / 1000:0.00} s | {Change(before.MeanMs, run.MeanMs)} |").AppendLine();
            var flips = run.Answers.Join(before.Answers, a => a.Id, a => a.Id, (a, p) => (a, p)).Where(x => x.a.Passed != x.p.Passed).ToList();
            if (flips.Count > 0)
            {
                b.AppendLine("Changed results:").AppendLine();
                foreach (var (a, _) in flips) b.AppendLine($"- **{a.Id}**: {(a.Passed ? "now passes" : "now fails, " + a.Why)}");
                b.AppendLine();
            }
        }
        b.AppendLine("| Case | Result | Tokens | Time | Answer |").AppendLine("|---|---|---:|---:|---|");
        foreach (var a in run.Answers)
            b.AppendLine($"| {a.Id} | {(a.Passed ? "pass" : "fail: " + a.Why)} | {a.Tokens:N0} | {a.Ms / 1000:0.00} s | {Cell(a.Answer)} |");
        return b.ToString();
    }

    static string Change(double before, double after) => before == 0 ? "—" : $"{(after - before) / before:+0%;-0%;0%}";
    static string Cell(string s) { s = s.Replace('\n', ' ').Replace("|", "\\|").Trim(); return s.Length > 120 ? s[..120] + "…" : s; }
}
