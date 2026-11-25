namespace AiPulse;

/// <summary>One Claude Code API request's reported cost. Claude Code puts cost_usd and prompt.id on its OTel api_request event.</summary>
public record ClaudeCost(string Id, long Time, string Model, string Session, string Prompt, double Cost);

/// <summary>Cost grouped by a key (a prompt, a model or a session).</summary>
public record CostGroup(string Key, long Requests, double Cost, long LastSeen);

public sealed partial class Storage
{
    void InitializeClaudeCosts() => Execute("""
        CREATE TABLE IF NOT EXISTS claude_costs(id TEXT PRIMARY KEY,time INTEGER NOT NULL,model TEXT NOT NULL,session TEXT NOT NULL,prompt TEXT NOT NULL,cost REAL NOT NULL);
        CREATE INDEX IF NOT EXISTS claude_costs_time ON claude_costs(time);
        """);

    /// <summary>Stores a reported cost. Repeats of the same request keep the larger value, matching how usage is stored.</summary>
    public void RecordClaudeCost(ClaudeCost c)
    {
        long now = J.Now;
        if (!double.IsFinite(c.Cost) || c.Cost < 0 || c.Cost > 10_000 || c.Time <= 0 || c.Time > now + 300 || c.Time < now - RetentionDays * 86400L) return;
        string prompt = c.Prompt.Length > 128 ? c.Prompt[..128] : c.Prompt;
        lock (gate) Execute("INSERT INTO claude_costs VALUES(@p0,@p1,@p2,@p3,@p4,@p5) ON CONFLICT(id) DO UPDATE SET cost=max(cost,excluded.cost)",
            c.Id, c.Time, c.Model, c.Session, prompt, c.Cost);
    }

    public double ClaudeCostSince(long since)
    {
        lock (gate) { using var c = Command("SELECT COALESCE(SUM(cost),0) FROM claude_costs WHERE time>=@p0", since); return Convert.ToDouble(c.ExecuteScalar()); }
    }

    /// <summary>Reported cost grouped by "prompt", "model" or "session", most expensive first.</summary>
    public List<CostGroup> ClaudeCostBy(string key, long since, int limit = 10)
    {
        string column = key switch { "prompt" => "prompt", "model" => "model", "session" => "session", _ => throw new ArgumentException(key) };
        lock (gate)
        {
            using var c = Command($"SELECT {column},COUNT(*),SUM(cost),MAX(time) FROM claude_costs WHERE time>=@p0 AND {column}<>'' GROUP BY {column} ORDER BY SUM(cost) DESC LIMIT @p1", since, limit);
            using var r = c.ExecuteReader();
            var list = new List<CostGroup>();
            while (r.Read()) list.Add(new(r.GetString(0), r.GetInt64(1), r.GetDouble(2), r.GetInt64(3)));
            return list;
        }
    }

    /// <summary>The largest single prompt (input tokens) per model since a time, to compare with the loaded context window.</summary>
    public Dictionary<string, long> LargestPrompt(long since)
    {
        lock (gate)
        {
            using var c = Command("SELECT model,MAX(input) FROM usage WHERE time>=@p0 AND model<>'' GROUP BY model", since);
            using var r = c.ExecuteReader();
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
            return map;
        }
    }

    /// <summary>Input plus output tokens across every source since a time.</summary>
    public long TokensSince(long since)
    {
        lock (gate) { using var c = Command("SELECT COALESCE(SUM(input+output),0) FROM usage WHERE time>=@p0", since); return Convert.ToInt64(c.ExecuteScalar()); }
    }
}

/// <summary>Daily budgets for tokens and reported Claude Code cost. A budget of 0 is off.</summary>
public static class Budgets
{
    public static long StartOfLocalDay(DateTimeOffset now) => new DateTimeOffset(now.Date, now.Offset).ToUnixTimeSeconds();

    public static (bool Over, string Detail) Tokens(long used, long budget) =>
        (budget > 0 && used >= budget, $"{J.Count(used)} tokens today against a daily budget of {J.Count(budget)}.");

    public static (bool Over, string Detail) Cost(double used, double budget) =>
        (budget > 0 && used >= budget, $"${used:0.00} of reported Claude Code cost today against a daily budget of ${budget:0.00}.");
}
