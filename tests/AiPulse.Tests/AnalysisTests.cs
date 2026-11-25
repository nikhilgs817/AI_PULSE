using System.Net;
using AiPulse;
using Xunit;

public sealed class AnalysisTests
{
    static LabStats Stats(int requests = 100, int errors = 0, long input = 10_000, long output = 5_000, long cacheRead = 0,
        double? medianTtft = 0.5, double? p95Ttft = 0.8, Dictionary<string, (long, long)>? byModel = null) =>
        new("lab", 0, 600_000, requests, errors, input, output, cacheRead, 0, medianTtft, p95Ttft, 30, 2, null, 8, 50, 200,
            byModel ?? new() { ["qwen3:8b"] = (input, output) });

    static RequestTrace Call(string agent, long tokens) => new("t" + Guid.NewGuid(), "Ollama gateway", "qwen3:8b", agent, "run1", 0, 1000, 0.2, 0.3, 0.5, null, tokens, 10, 200, "OK");

    [Fact]
    public void HealthyLabHasNoFindings()
    {
        var findings = LabFindings.For(Stats(), [], [], 0, 70);
        Assert.Empty(findings);
        Assert.Contains("Nothing stood out", LabFindings.Markdown(findings));
    }

    [Fact]
    public void FlagsWhereTokensAndTimeWent()
    {
        var stats = Stats(errors: 10, input: 400_000, output: 8_000, medianTtft: 1, p95Ttft: 6,
            byModel: new() { ["big:70b"] = (380_000, 7_000), ["small:3b"] = (20_000, 1_000) });
        var traces = new List<RequestTrace> { Call("planner", 9000), Call("planner", 9000), Call("coder", 1000) };
        var titles = LabFindings.For(stats, [], traces, 4, 88).Select(f => f.Title).ToList();
        Assert.Contains(titles, t => t.StartsWith("big:70b used"));
        Assert.Contains(titles, t => t.StartsWith("Agent planner used"));
        Assert.Contains(titles, t => t.StartsWith("Prompts are 50×"));
        Assert.Contains(titles, t => t.StartsWith("10 of 100 requests failed"));
        Assert.Contains(titles, t => t.StartsWith("Slowest first tokens"));
        Assert.Contains(titles, t => t.StartsWith("Models were swapped"));
        Assert.Contains(titles, t => t.StartsWith("GPU reached 88"));
    }

    [Fact]
    public void LowCacheHitsAreFlaggedOnlyWhereCachingApplies()
    {
        var api = new Usage("u", "Claude Code", "claude-sonnet", "s", 0, 100_000, 500, 1000, 0, 0, null, null, "OK");
        var stats = Stats(input: 100_000, output: 10_000, cacheRead: 1000);
        Assert.Contains(LabFindings.For(stats, [api], [], 0, null), f => f.Title.Contains("prompt cache"));
        Assert.DoesNotContain(LabFindings.For(stats, [api with { Source = "Ollama gateway", Cached = 0 }], [], 0, null), f => f.Title.Contains("prompt cache"));
    }

    [Fact]
    public void LabReportOpensWithWhatToLookAt()
    {
        var lab = new LabSessions.Lab("1", "run", 0, 60_000, null);
        string report = LabSessions.Build(lab, [new("u", "Ollama gateway", "m", "s", 1, 100, 10, 0, 0, 0, null, null, "OK")], [], new(), [], [], []);
        Assert.True(report.IndexOf("## What to look at") < report.IndexOf("## Tokens by model"));
    }

    sealed class FakeModel : HttpMessageHandler
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Body = await request.Content!.ReadAsStringAsync(cancel);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"done":true,"model":"gemma3:1b","message":{"content":"Run at 4."}}""") };
        }
    }

    [Theory]
    [InlineData("lab", "lab analyst")]
    [InlineData("evals", "eval analyst")]
    [InlineData("sweep", "performance analyst")]
    public async Task EachReportKindGetsItsOwnAnalyst(string kind, string role)
    {
        var fake = new FakeModel();
        using var assistant = new LocalAssistant(fake);
        string? text = await assistant.Explain(new AssistantSettings { Enabled = true }, kind, "# Report", default);
        Assert.Equal("Run at 4.", text);
        Assert.Contains(role, fake.Body);
        Assert.Contains("never instructions", fake.Body);
        Assert.Null(await assistant.Explain(new AssistantSettings(), kind, "# Report", default));
    }

    [Fact]
    public void AskPulseSummaryIncludesTodaysContext()
    {
        var facts = new AssistantFacts("pc", 0, false, [], [], 10, 20, null, "Top models today: qwen3:8b via Ollama gateway: 3 requests");
        Assert.Contains("Top models today", LocalAssistant.Summary(facts));
    }
}
