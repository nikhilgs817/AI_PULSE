using System.Globalization;

namespace AiPulse;

/// <summary>
/// Plain rules that read a lab's numbers and say where the tokens and time went and what to try next.
/// They work without any AI model, so every lab report gets them; the local assistant, when on, adds its own reading.
/// </summary>
public static class LabFindings
{
    public sealed record Finding(string Title, string Detail, string Try);

    public static List<Finding> For(LabStats stats, List<Usage> usage, List<RequestTrace> traces, int evictions, double? peakGpuTemperature)
    {
        var inv = CultureInfo.InvariantCulture;
        var list = new List<Finding>();
        if (stats.Requests == 0) return list;

        var top = stats.ByModel.OrderByDescending(m => m.Value.Input + m.Value.Output).FirstOrDefault();
        if (stats.Tokens > 0 && stats.ByModel.Count > 1 && (double)(top.Value.Input + top.Value.Output) / stats.Tokens is var share && share >= 0.6)
            list.Add(new($"{top.Key} used {share:P0} of all tokens", $"{top.Value.Input + top.Value.Output:N0} of {stats.Tokens:N0} tokens went to one model.",
                "Start there: check whether every call to it needs the full context, or whether a smaller model can take the simple steps."));

        var agents = traces.Where(t => t.Agent != "" && t.Outcome != "Agent run").GroupBy(t => t.Agent)
            .Select(g => (Agent: g.Key, Tokens: g.Sum(t => t.PromptTokens + t.OutputTokens), Calls: g.Count())).OrderByDescending(a => a.Tokens).ToList();
        long agentTokens = agents.Sum(a => a.Tokens);
        if (agents.Count > 1 && agentTokens > 0 && (double)agents[0].Tokens / agentTokens >= 0.5)
            list.Add(new($"Agent {agents[0].Agent} used {(double)agents[0].Tokens / agentTokens:P0} of agent tokens", $"{agents[0].Calls:N0} model calls, {agents[0].Tokens / Math.Max(1, agents[0].Calls):N0} tokens per call on average.",
                "Look at what it sends on each call; tool results and chat history pasted back in are the usual cause."));

        if (stats.Output > 0 && stats.Input / (double)stats.Output is var ratio && ratio >= 20 && stats.Input >= 20_000)
            list.Add(new($"Prompts are {ratio.ToString("0", inv)}× the size of the answers", $"{stats.Input:N0} input tokens for {stats.Output:N0} output.",
                "Trim the context: summarise history, send only the relevant files or rows, and cap tool output before it goes back to the model."));

        if (stats.CacheHitRatio is double hit && stats.Input >= 50_000 && hit < 0.2 && usage.Any(u => u.Cached > 0 || u.Source.Contains("Claude") || u.Source.Contains("OpenAI")))
            list.Add(new($"Only {hit:P0} of input came from the prompt cache", "Repeated prompt prefixes are being paid for in full.",
                "Keep the system prompt and tool definitions identical and first in every call, and put the changing part at the end."));

        if (stats.ErrorRate is double errors && errors >= 0.05)
            list.Add(new($"{stats.Errors:N0} of {stats.Requests:N0} requests failed ({errors:P0})", "Failed calls still cost time, and retries cost tokens again.",
                "Open the Agents timeline for the failing calls; time-outs usually mean the model is overloaded or still loading."));

        if (stats.MedianTtft is double median && stats.P95Ttft is double p95 && median > 0 && p95 >= 3 * median && p95 >= 2)
            list.Add(new($"Slowest first tokens took {p95.ToString("0.0", inv)} s, {(p95 / median).ToString("0", inv)}× the median", "Some requests waited much longer than the rest.",
                "That is usually a cold model load or requests queueing. Run the load sweep to find how many at once your server handles."));

        if (evictions >= ModelResidency.ThrashEvictions)
            list.Add(new($"Models were swapped out of GPU memory {evictions} times", "Each swap reloads a model, which costs seconds per call.",
                "Use fewer models in the lab, a smaller quantisation, or set OLLAMA_MAX_LOADED_MODELS to what fits in VRAM."));

        if (peakGpuTemperature is double temperature && temperature >= 85)
            list.Add(new($"GPU reached {temperature.ToString("0", inv)} °C", "Hot GPUs lower their clocks, which slows generation.",
                "Check the GPU card on the Hardware page for throttling, and improve airflow or cap power before comparing speed."));

        return list;
    }

    public static string Markdown(List<Finding> findings)
    {
        if (findings.Count == 0) return "Nothing stood out: no single model or agent dominated, errors were rare and the cache and GPU looked healthy.\n";
        var b = new System.Text.StringBuilder();
        foreach (var f in findings) b.AppendLine($"- **{f.Title}.** {f.Detail} Try: {f.Try}");
        return b.ToString();
    }
}
