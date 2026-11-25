using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AiPulse;

// Agents page: the model bench card (load sweep and evals against a local Ollama model).
public sealed partial class MainWindow
{
    string benchModel = "", benchSuite = "", benchProgress = "", benchNotice = "";
    CancellationTokenSource? benchWork;
    SweepResult? lastSweep;
    EvalRun? lastEval, lastEvalBefore;
    string? lastBenchReport;

    /// <summary>Where bench requests go: through the gateway when it is on, so they show up in labs and on the timeline.</summary>
    string BenchUrl => localService.Settings.EnableGateway ? $"http://127.0.0.1:{localService.Settings.GatewayPort}" : localService.Settings.OllamaUrl;

    Control BenchCard()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Model bench", 16, null, true));
        stack.Children.Add(Text("A load sweep finds how many requests at once your model server handles before it slows down. Evals score a model's answers, so you can see whether a fix kept quality as well as cutting tokens. Run them inside a lab session to get both in one report.", 12, "#8ba0bd"));

        var models = localService.Ollama.Installed.Where(m => m.Host == "This PC").Select(m => m.Name).Distinct().Order().ToList();
        if (models.Count == 0) { stack.Children.Add(Text("No local Ollama models found. Start Ollama and pull a model to use the bench.", 12, "#e8b65f")); return Card(stack); }
        if (!models.Contains(benchModel)) benchModel = models[0];
        Evals.EnsureSample();
        var suites = Evals.Suites().ToList();
        if (!suites.Contains(benchSuite)) benchSuite = suites.FirstOrDefault() ?? "";

        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        var modelBox = new ComboBox { ItemsSource = models, SelectedItem = benchModel, MinWidth = 220, IsEnabled = benchWork == null };
        Avalonia.Automation.AutomationProperties.SetName(modelBox, "Bench model");
        modelBox.SelectionChanged += (_, _) => { if (modelBox.SelectedItem is string m) benchModel = m; };
        row.Children.Add(modelBox);
        if (benchWork != null)
        {
            var stop = Btn("Stop"); stop.Click += (_, _) => benchWork?.Cancel(); row.Children.Add(stop);
            row.Children.Add(Text(benchProgress + "…", 13, "#72d4e8", true));
            stack.Children.Add(row);
            return Card(stack);
        }
        var sweep = Btn("Run load sweep"); ToolTip.SetTip(sweep, $"Sends 8 requests at 1, 2, 4 and 8 at a time to {benchModel}");
        sweep.Click += (_, _) => BenchRun("Load sweep", "sweep", async (http, progress, cancel) =>
        {
            var result = await LoadSweep.Run(http, BenchUrl, benchModel, LoadSweep.Levels, 8, 128, progress, cancel);
            lastSweep = result;
            return WriteExport($"sweep-{Slug(result.Model)}-{result.Time:yyyyMMdd-HHmm}.md", LoadSweep.Markdown(result));
        });
        row.Children.Add(sweep);
        if (suites.Count > 0)
        {
            var suiteBox = new ComboBox { ItemsSource = suites, SelectedItem = benchSuite, MinWidth = 140 };
            Avalonia.Automation.AutomationProperties.SetName(suiteBox, "Eval suite");
            suiteBox.SelectionChanged += (_, _) => { if (suiteBox.SelectedItem is string s) benchSuite = s; };
            row.Children.Add(suiteBox);
            var evals = Btn("Run evals"); ToolTip.SetTip(evals, "Runs every case in the suite once and scores the answers");
            evals.Click += (_, _) => BenchRun("Evals", "evals", async (http, progress, cancel) =>
            {
                var cases = Evals.Parse(await File.ReadAllTextAsync(Path.Combine(Evals.Folder, benchSuite + ".jsonl"), cancel));
                if (cases.Count == 0) throw new FormatException("That suite has no cases.");
                var run = await Evals.Run(http, BenchUrl, benchModel, benchSuite, cases, 256, progress, cancel);
                Evals.Save(run);
                var before = Evals.Previous(run);
                lastEval = run; lastEvalBefore = before;
                return WriteExport($"evals-{Slug(run.Suite)}-{Slug(run.Model)}-{DateTimeOffset.FromUnixTimeMilliseconds(run.Time).ToLocalTime():yyyyMMdd-HHmm}.md", Evals.Markdown(run, before));
            });
            row.Children.Add(evals);
        }
        var folder = Btn("Edit evals"); ToolTip.SetTip(folder, "Opens the folder of eval suites: one .jsonl file per suite, one case per line");
        folder.Click += (_, _) => OpenFolder(Evals.Folder);
        row.Children.Add(folder);
        stack.Children.Add(row);
        if (benchNotice != "") stack.Children.Add(Text(benchNotice, 12, "#e8b65f"));

        if (lastSweep is { } s)
        {
            stack.Children.Add(Text($"Load sweep · {s.Model} · {LoadSweep.Verdict(s)}", 13, null, true));
            stack.Children.Add(Table(["At once", "Requests/s", "Tokens/s", "p50", "p95", "Errors"],
                s.Points.Select(p => new[] { p.Concurrency.ToString(), p.RequestsPerSecond.ToString("0.00"), p.TokensPerSecond.ToString("0.0"), Seconds(p.P50Ms / 1000), Seconds(p.P95Ms / 1000), p.Errors.ToString() }),
                "*,*,*,*,*,*"));
        }
        if (lastEval is { } e)
        {
            string trend = lastEvalBefore is { } b ? $" · before {b.Passed} of {b.Answers.Count}" + (e.PassRate > b.PassRate ? ", better" : e.PassRate < b.PassRate ? ", worse" : ", same") : " · first run of this suite on this model";
            stack.Children.Add(Text($"Evals · {e.Suite} on {e.Model} · {e.Passed} of {e.Answers.Count} passed{trend}", 13, e.PassRate >= 1 ? "#6ee7a8" : null, true));
            var failing = e.Answers.Where(a => !a.Passed).Take(6).Select(a => new[] { a.Id, a.Why, a.Answer.Replace('\n', ' ') is var t && t.Length > 80 ? t[..80] + "…" : a.Answer.Replace('\n', ' ') }).ToList();
            if (failing.Count > 0) stack.Children.Add(Table(["Failed case", "Why", "Answer"], failing, "*,1.4*,3*"));
        }
        if (lastBenchReport is { } report && File.Exists(report)) { var open = Btn("Open last bench report"); open.Click += (_, _) => OpenFile(report); stack.Children.Add(open); }
        return Card(stack);
    }

    void BenchRun(string what, string kind, Func<HttpClient, Action<string>, CancellationToken, Task<string>> work)
    {
        if (benchWork != null) return;
        benchWork = new CancellationTokenSource(); benchProgress = what; benchNotice = "";
        var cancel = benchWork.Token;
        RenderBody();
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            try { var report = await work(http, p => benchProgress = what + ": " + p, cancel); if (localService.Settings.Assistant.Enabled) { benchProgress = what + ": local AI is reading the results"; await AppendAnalysis(report, kind); } lastBenchReport = report; }
            catch (OperationCanceledException) { benchNotice = what + " stopped."; }
            catch (Exception e) when (e is HttpRequestException or IOException or FormatException or InvalidOperationException) { benchNotice = what + " failed: " + e.Message; Paths.Log("bench", e); }
            await Dispatcher.UIThread.InvokeAsync(() => { benchWork?.Dispose(); benchWork = null; RenderBody(); });
        });
    }
}
