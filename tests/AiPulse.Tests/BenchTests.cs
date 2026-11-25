using System.Net;
using System.Text;
using AiPulse;
using Xunit;

public sealed class BenchTests
{
    static SweepPoint P(int c, double tps, double p95, int errors = 0) => new(c, 8, errors, p95 / 2, p95, tps, tps / 10);

    [Fact]
    public void KneeIsTheLastLevelThatStillScaled()
    {
        Assert.Equal(4, LoadSweep.Knee([P(1, 20, 1000), P(2, 38, 1100), P(4, 70, 1300), P(8, 72, 2600)]));
        Assert.Equal(2, LoadSweep.Knee([P(1, 20, 1000), P(2, 38, 1100), P(4, 80, 2500)]));
        Assert.Equal(1, LoadSweep.Knee([P(1, 20, 1000), P(2, 40, 1000, errors: 1)]));
        Assert.Null(LoadSweep.Knee([P(1, 20, 1000), P(2, 40, 1100), P(4, 79, 1200)]));
    }

    [Fact]
    public void PercentileInterpolates()
    {
        Assert.Equal(15, LoadSweep.Percentile([10, 20], 50));
        Assert.Equal(40, LoadSweep.Percentile([10, 20, 30, 40], 100));
        Assert.Equal(0, LoadSweep.Percentile([], 95));
    }

    [Fact]
    public void ParsesSuitesAndReportsBadLines()
    {
        var cases = Evals.Parse("# comment\n\n{\"prompt\":\"2+2?\",\"contains\":\"4\"}\n{\"id\":\"j\",\"prompt\":\"x\",\"regex\":\"ok\\\\s*:\"}\n");
        Assert.Equal(2, cases.Count);
        Assert.Equal("case-1", cases[0].Id);
        Assert.Equal("ok\\s*:", cases[1].Regex);
        Assert.Contains("Line 2", Assert.Throws<FormatException>(() => Evals.Parse("\n{\"id\":\"no prompt\"}")).Message);
        Assert.Contains("Line 1", Assert.Throws<FormatException>(() => Evals.Parse("{\"prompt\":\"x\",\"regex\":\"(\"}")).Message);
    }

    [Fact]
    public void ScoringNeedsEveryCheck()
    {
        var c = new EvalCase("c", "p", "Canberra", "^\\w+\\.?$", "sorry");
        Assert.True(Evals.Score(c, "canberra.").Passed);
        Assert.False(Evals.Score(c, "Sydney").Passed);
        Assert.False(Evals.Score(c, "Canberra, I think").Passed);
        Assert.False(Evals.Score(c with { Regex = null }, "Sorry, Canberra").Passed);
        Assert.False(Evals.Score(new EvalCase("e", "p", null, null, null), "  ").Passed);
    }

    sealed class FakeOllama(Func<string, string> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Interlocked.Increment(ref Calls);
            string body = await request.Content!.ReadAsStringAsync(cancel);
            string text = answer(body);
            await Task.Delay(5, cancel);
            if (text == "500") return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("model not found") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"response\":\"{text}\",\"eval_count\":10}}", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task SweepRunsEveryLevelAndCountsTokens()
    {
        var fake = new FakeOllama(_ => "ok");
        using var http = new HttpClient(fake);
        var result = await LoadSweep.Run(http, "http://127.0.0.1:1", "m", [1, 2, 4], 6, 32, _ => { }, default);
        Assert.Equal(3, result.Points.Count);
        Assert.Equal(1 + 18, fake.Calls);
        Assert.All(result.Points, p => { Assert.Equal(0, p.Errors); Assert.True(p.TokensPerSecond > 0); });
    }

    [Fact]
    public async Task SweepStopsWhenEveryRequestFails()
    {
        using var http = new HttpClient(new FakeOllama(_ => "500"));
        var result = await LoadSweep.Run(http, "http://127.0.0.1:1", "missing", [1, 2], 3, 32, _ => { }, default);
        Assert.Single(result.Points);
        Assert.Contains("Every request failed", LoadSweep.Verdict(result));
    }

    [Fact]
    public async Task EvalRunsCompareWithThePreviousRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "aipulse-evals-" + Guid.NewGuid().ToString("N"));
        Paths.Initialize(["--data-dir", root]);
        try
        {
            var cases = new List<EvalCase> { new("a", "say yes", "yes", null, null), new("b", "say no", "no", null, null) };
            using var first = new HttpClient(new FakeOllama(_ => "yes"));
            var before = await Evals.Run(first, "http://x", "m:1", "suite", cases, 16, _ => { }, default);
            Evals.Save(before);
            await Task.Delay(5);
            using var second = new HttpClient(new FakeOllama(body => body.Contains("say no") ? "no" : "yes"));
            var after = await Evals.Run(second, "http://x", "m:1", "suite", cases, 16, _ => { }, default);
            Evals.Save(after);
            Assert.Equal(1, before.Passed);
            Assert.Equal(2, after.Passed);
            var previous = Evals.Previous(after);
            Assert.NotNull(previous);
            Assert.Equal(before.Time, previous!.Time);
            string report = Evals.Markdown(after, previous);
            Assert.Contains("**b**: now passes", report);
            Assert.Contains("+50 pts", report);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
