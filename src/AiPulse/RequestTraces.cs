using System.Text.RegularExpressions;

namespace AiPulse;

// Per-request timing and attribution, recorded beside the token ledger (same Id as the usage row).
// Timestamps are Unix milliseconds. Durations are seconds; null means "not reported", never zero.
// Agent/Run come from X-Agent-Name / X-Agent-Run request headers or OTel agent spans.
public record RequestTrace(string Id, string Source, string Model, string Agent, string Run,
    long StartMs, long EndMs, double? TtftSeconds, double? PromptSeconds, double? GenerateSeconds, double? LoadSeconds,
    long PromptTokens, long OutputTokens, int HttpStatus, string Outcome)
{
    public long Time => EndMs / 1000;
    public double? PromptRate => PromptSeconds is > 0 && PromptTokens > 0 ? PromptTokens / PromptSeconds : null;
    public double? GenerateRate => GenerateSeconds is > 0 && OutputTokens > 0 ? OutputTokens / GenerateSeconds : null;
}

public static partial class AgentLabel
{
    [GeneratedRegex(@"^[\p{L}\p{N} _.:/@#+-]{1,80}$")]
    private static partial Regex Safe();
    // Header values are untrusted: keep short printable identifiers only.
    public static string Clean(string? value)
    {
        value = value?.Trim() ?? "";
        return value.Length > 0 && Safe().IsMatch(value) ? value : "";
    }
}

public sealed partial class Storage
{
    void InitializeRequestTraces() => Execute("""
        CREATE TABLE IF NOT EXISTS request_traces(id TEXT PRIMARY KEY,source TEXT NOT NULL,model TEXT NOT NULL,agent TEXT NOT NULL,run TEXT NOT NULL,
            start_ms INTEGER NOT NULL,end_ms INTEGER NOT NULL,ttft REAL,prompt_s REAL,generate_s REAL,load_s REAL,
            prompt_tokens INTEGER NOT NULL,output_tokens INTEGER NOT NULL,http_status INTEGER NOT NULL,outcome TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS request_traces_end ON request_traces(end_ms);
        """);

    public void PutTrace(RequestTrace t)
    {
        long now = J.Now;
        if (t.Time < now - RetentionDays * 86400L || t.Time > now + 300 || t.EndMs < t.StartMs) return;
        lock (gate) { using var tx = db.BeginTransaction(); TraceCore(t); AppendEvent("request-trace", t); tx.Commit(); }
    }

    void TraceCore(RequestTrace t) => Execute("""
        INSERT INTO request_traces VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14)
        ON CONFLICT(id) DO UPDATE SET model=excluded.model,agent=excluded.agent,run=excluded.run,end_ms=excluded.end_ms,ttft=coalesce(excluded.ttft,ttft),
            prompt_s=coalesce(excluded.prompt_s,prompt_s),generate_s=coalesce(excluded.generate_s,generate_s),load_s=coalesce(excluded.load_s,load_s),
            prompt_tokens=max(prompt_tokens,excluded.prompt_tokens),output_tokens=max(output_tokens,excluded.output_tokens),http_status=excluded.http_status,outcome=excluded.outcome
        """, t.Id, t.Source, t.Model, t.Agent, t.Run, t.StartMs, t.EndMs, t.TtftSeconds, t.PromptSeconds, t.GenerateSeconds, t.LoadSeconds,
        Math.Max(0, t.PromptTokens), Math.Max(0, t.OutputTokens), t.HttpStatus, t.Outcome);

    internal static void ValidateTrace(RequestTrace t)
    {
        static void Text(string s, int max = 512) { if (s.Length > max || s.Any(char.IsControl)) throw new InvalidDataException(); }
        Text(t.Id); Text(t.Source); Text(t.Model); Text(t.Agent, 80); Text(t.Run, 80); Text(t.Outcome);
        if (t.StartMs <= 0 || t.EndMs < t.StartMs || t.EndMs - t.StartMs > 7L * 86400_000 || t.PromptTokens is < 0 or > 1_000_000_000_000 || t.OutputTokens is < 0 or > 1_000_000_000_000)
            throw new InvalidDataException();
        foreach (var d in new[] { t.TtftSeconds, t.PromptSeconds, t.GenerateSeconds, t.LoadSeconds })
            if (d is { } v && (!double.IsFinite(v) || v < 0 || v > 7 * 86400)) throw new InvalidDataException();
    }

    public List<RequestTrace> Traces(long sinceMs, long untilMs = long.MaxValue, int limit = 20000)
    {
        lock (gate)
        {
            using var c = Command("SELECT * FROM request_traces WHERE end_ms BETWEEN @p0 AND @p1 ORDER BY start_ms LIMIT @p2", sinceMs, untilMs, limit);
            using var r = c.ExecuteReader();
            var list = new List<RequestTrace>();
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            while (r.Read())
                list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6),
                    D(7), D(8), D(9), D(10), r.GetInt64(11), r.GetInt64(12), r.GetInt32(13), r.GetString(14)));
            return list;
        }
    }
}
