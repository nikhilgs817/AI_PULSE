using System.Diagnostics;

namespace AiPulse;

public sealed partial class MainWindow
{
    // Measures what one timer tick costs on each page: building or updating the page plus a layout
    // pass. Runs only after the UI checks when AIPULSE_RENDER_BENCH is set, so it never runs for users.
    async Task RunRenderBench()
    {
        const int ticks = 30;
        var lines = new List<string> { "page\tms_per_tick\tbuild_only_ms\tcontrols\tpatched\tswapped" };
        bool wasCompact = localService.Settings.Compact;
        foreach (var target in new[] { "Compact", "Overview", "Hardware", "Sensors", "Models", "Agents", "Token", "History", "Alerts" })
        {
            localService.Settings.Compact = target == "Compact";
            if (target != "Compact") page = target;
            ApplyWindowLayout(); layoutPage = null; RenderBody();
            await Task.Delay(300);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++) { TimerTick(); UpdateLayout(); }
            clock.Stop();
            var build = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++) _ = target switch { "Compact" => Compact(), "Overview" => Overview(), "Hardware" => HardwarePage(), "Sensors" => SensorsPage(), "Models" => ModelsPage(), "Agents" => AgentsPage(), "Token" => TokenPage(), "History" => HistoryPage(), _ => AlertsPage() };
            build.Stop();
            TimerTick(); UpdateLayout();
            int controls = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).Count();
            lines.Add($"{target}\t{clock.Elapsed.TotalMilliseconds / ticks:0.00}\t{build.Elapsed.TotalMilliseconds / ticks:0.00}\t{controls}\t{Reconciler.LastStats.Patched}\t{Reconciler.LastStats.Swapped}");
        }
        localService.Settings.Compact = wasCompact;
        File.WriteAllLines(Path.Combine(Paths.Root, "render-bench.txt"), lines);
    }
}
