using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiPulse;

internal sealed record QuotaWindow(double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset? ObservedAt = null)
{
    internal double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record ProviderQuota(QuotaWindow? FiveHour, QuotaWindow? Weekly, DateTimeOffset? ObservedAt)
{
    internal static ProviderQuota Merge(DateTimeOffset now, params ProviderQuota?[] snapshots)
    {
        QuotaWindow? Latest(Func<ProviderQuota, QuotaWindow?> select)
        {
            return snapshots.OfType<ProviderQuota>()
                .Select(q => select(q) is { } w ? w with { ObservedAt = w.ObservedAt ?? q.ObservedAt } : null)
                .OfType<QuotaWindow>()
                .Where(w => NotReset(w, now) && w.ObservedAt is { } at && at <= now.AddMinutes(5))
                .OrderByDescending(w => w.ObservedAt)
                .FirstOrDefault();
        }
        var shortWindow = Latest(q => q.FiveHour);
        var longWindow = Latest(q => q.Weekly);
        return new(shortWindow, longWindow, new[] { shortWindow?.ObservedAt, longWindow?.ObservedAt }.Max());
    }

    // A window with no reset time has not started yet (0% used); its age alone decides freshness.
    internal static bool NotReset(QuotaWindow window, DateTimeOffset now) => window.ResetsAt is not { } reset || reset > now;

    internal static bool IsLastReported(QuotaWindow? window, DateTimeOffset now) =>
        window?.ObservedAt is { } seen && now - seen > TimeSpan.FromMinutes(15);

    internal static QuotaWindow? FreshWindow(QuotaWindow? window, DateTimeOffset now) =>
        window?.ObservedAt is { } at && at <= now.AddMinutes(5) && NotReset(window, now) && !IsLastReported(window, now)
            ? window
            : null;

    // For display: a provider-reported reading stays valid until its window resets, because only the
    // user's own usage moves it. Older readings are shown as "last reported", not replaced by a dash.
    internal static QuotaWindow? ShownWindow(QuotaWindow? window, DateTimeOffset now) =>
        window?.ObservedAt is { } at && at <= now.AddMinutes(5) && NotReset(window, now) ? window : null;

    internal bool NeedsRefresh(DateTimeOffset now) =>
        FreshWindow(FiveHour, now) is null || FreshWindow(Weekly, now) is null;
}

public sealed partial class MainWindow
{
    ProviderQuota codexQuota = new(null, null, null);
    ProviderQuota claudeQuota = new(null, null, null);
    DateTimeOffset nextQuotaRead;
    bool quotaReadPending;
    DateTimeOffset nextManualQuotaRead;
    string codexQuotaStatus = "Waiting for live Codex usage.";
    readonly QuotaForecast forecasts = new();
    readonly Dictionary<string, bool> quotaAlerts = [];
    readonly HashSet<string> quotaNotified = [];
    Avalonia.Controls.Notifications.WindowNotificationManager? notifications;

    IEnumerable<(string Key, string Provider, string Period, QuotaWindow? Window)> QuotaWindows() =>
    [
        ("codex-5h", "ChatGPT", "5-hour", codexQuota.FiveHour), ("codex-week", "ChatGPT", "weekly", codexQuota.Weekly),
        ("claude-5h", "Claude", "5-hour", claudeQuota.FiveHour), ("claude-week", "Claude", "weekly", claudeQuota.Weekly),
    ];

    // Burn-rate projection per window; warns at 80% used or when the limit is under an hour away.
    void TrackQuotaForecasts(DateTimeOffset now)
    {
        foreach (var (key, provider, period, window) in QuotaWindows())
        {
            if (window is null) continue;
            forecasts.Add(key, window, now);
            var projection = forecasts.Project(key, now);
            bool soon = projection?.LimitAt is { } hit && (projection.ResetsAt is null || hit < projection.ResetsAt) && hit - now < TimeSpan.FromHours(1);
            bool active = window.UsedPercent >= 80 || soon;
            string title = $"{provider} {period} usage at {window.UsedPercent:0}%";
            string detail = $"{provider} {period} window: {window.UsedPercent:0}% used" + (projection is null ? "" : "; " + projection.Describe(now)) +
                (window.ResetsAt is { } reset ? $". Resets {reset.ToLocalTime():MMM d, h:mm tt}." : ".");
            // Store only transitions and active updates; quiet windows do not write every minute.
            if (active || quotaAlerts.GetValueOrDefault(key))
            {
                try { localService.Store.Alert("quota:" + key, title, detail, active); } catch (Exception e) { Paths.Log("quota-alert", e); }
                quotaAlerts[key] = active;
            }
            if (active && quotaNotified.Add(key + ":" + (window.ResetsAt?.ToUnixTimeSeconds() ?? 0)))
            {
                notifications ??= new(this) { Position = Avalonia.Controls.Notifications.NotificationPosition.TopRight, MaxItems = 2 };
                notifications.Show(new Avalonia.Controls.Notifications.Notification(title, detail, Avalonia.Controls.Notifications.NotificationType.Warning, TimeSpan.FromSeconds(20)));
            }
        }
    }
    DateTimeOffset nextCodexLiveRead;
    TimeSpan codexLiveDelay = TimeSpan.FromMinutes(1);
    string codexLiveStatus = "Waiting for live Codex usage.";

    async Task RefreshSubscriptionQuotas(DateTimeOffset now, bool manual = false)
    {
        if (quotaReadPending || !localService.Settings.ReadAgentUsage) return;
        if (manual) nextManualQuotaRead = now.AddSeconds(15);
        nextQuotaRead = now.AddMinutes(1);
        quotaReadPending = true;
        try
        {
            var results = await Task.Run(async () =>
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(home, ".codex");
                // Session logs carry the same provider-reported windows for CLI, IDE and desktop Codex.
                // They are the primary source; the live CLI call only refreshes them when available.
                ProviderQuota? fromSessions = null;
                try { fromSessions = CodexQuotaReader.Read(codexHome, now); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Paths.Log("codex-quota-sessions", e); }
                ProviderQuota? live = null;
                string liveStatus;
                if (now < nextCodexLiveRead) liveStatus = codexLiveStatus;
                else
                {
                    try { live = await CodexLiveQuota.Read(); liveStatus = "Live Codex account usage."; codexLiveDelay = TimeSpan.FromMinutes(1); }
                    catch (FileNotFoundException) { liveStatus = "From Codex session logs (no codex command on PATH; set AIPULSE_CODEX for live reads)."; codexLiveDelay = TimeSpan.FromMinutes(30); }
                    catch (Exception e)
                    {
                        liveStatus = e is OperationCanceledException ? "Live Codex read timed out; showing session-log readings." : "Live Codex read failed; showing session-log readings.";
                        codexLiveDelay = TimeSpan.FromMinutes(Math.Min(30, codexLiveDelay.TotalMinutes * 2)); Paths.Log("codex-quota", e);
                    }
                    nextCodexLiveRead = now + codexLiveDelay; codexLiveStatus = liveStatus;
                }
                var codex = ProviderQuota.Merge(now, live, fromSessions, codexQuota);
                codexQuotaStatus = live != null ? liveStatus
                    : fromSessions?.ObservedAt == null ? "No Codex usage reading yet. Readings appear after your next Codex turn."
                    : liveStatus.StartsWith("Live Codex account") ? "From Codex session logs."
                    : liveStatus;
                // Claude Code's status line is the primary source: it writes provider-reported windows after
                // every reply with no network call. The rate-limited usage endpoint is asked only when those
                // readings are missing or older than 15 minutes, and the reader throttles itself further
                // (15-minute interval, back-off, Retry-After). A skipped or failed call keeps earlier readings.
                var cached = ReadClaudeQuota(now);
                ProviderQuota? remote = null;
                if (ClaudeQuotaReader.ShouldCallEndpoint(ProviderQuota.Merge(now, cached, claudeQuota), now))
                    remote = await ClaudeQuotaReader.Read(now, manual,
                        allowRenewal: ClaudeQuotaReader.RenewalAllowed(ClaudeQuotaStatusLine.LastSeen(), now));
                else ClaudeQuotaReader.UsingStatusLine();
                if (remote is not null)
                {
                    try { ClaudeQuotaStatusLine.Store(remote); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { Paths.Log("claude-quota-cache", e); }
                }
                var bestClaude = ProviderQuota.Merge(now, remote, cached, claudeQuota);
                return (codex, bestClaude);
            });
            (codexQuota, claudeQuota) = results;
            TrackQuotaForecasts(DateTimeOffset.UtcNow);
        }
        catch (Exception e) { Paths.Log("subscription-refresh", e); }
        finally
        {
            quotaReadPending = false;
            if (IsVisible && (localService.Settings.Compact ? ShowCompactQuotas : page == "Token" && !service.IsRemote))
                RenderBody();
        }
    }

    static ProviderQuota ReadClaudeQuota(DateTimeOffset now)
    {
        try
        {
            var snapshot = ClaudeQuotaStatusLine.ReadCache(ClaudeQuotaStatusLine.CachePath());
            if (snapshot is null || snapshot.ObservedAt <= 0 || snapshot.ObservedAt > now.AddMinutes(5).ToUnixTimeSeconds())
                return new(null, null, null);

            QuotaWindow? Convert(ClaudeQuotaStatusLine.Window? window)
            {
                if (window is null) return null;
                var observed = DateTimeOffset.FromUnixTimeSeconds(window.ObservedAt);
                DateTimeOffset? reset = window.ResetsAt is { } r ? DateTimeOffset.FromUnixTimeSeconds(r) : null;
                if (reset <= now || observed > now.AddMinutes(5)) return null;
                return new(window.UsedPercentage, reset, observed);
            }

            return new(Convert(snapshot.FiveHour),
                Convert(snapshot.SevenDay), DateTimeOffset.FromUnixTimeSeconds(snapshot.ObservedAt));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, null, null);
        }
    }

    Control SubscriptionQuotaCard()
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(Text("Subscription usage", 15, null, true));
        heading.Children.Add(Text("% left", 11, "#9eb1ca"));
        var row = SubscriptionQuotaRow(compact: false);
        var refresh = Btn(quotaReadPending ? "Refreshing…" : "Refresh usage");
        refresh.IsEnabled = localService.Settings.ReadAgentUsage && !quotaReadPending && DateTimeOffset.UtcNow >= nextManualQuotaRead;
        refresh.Click += (_, _) =>
        {
            if (quotaReadPending || DateTimeOffset.UtcNow < nextManualQuotaRead) return;
            _ = RefreshSubscriptionQuotas(DateTimeOffset.UtcNow, manual: true);
            RenderBody();
        };
        heading.Children.Add(refresh);
        var content = new StackPanel { Spacing = 8, Children = { heading, row } };
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, provider, period, window) in QuotaWindows())
            if (window is not null && forecasts.Project(key, now) is { } projection)
                content.Children.Add(Text($"{provider} {period}: {projection.Describe(now)}", 12, projection.LimitAt is { } hit && (projection.ResetsAt is null || hit < projection.ResetsAt) ? "#e8b65f" : "#b3c8e2"));
        content.Children.Add(Text(codexQuotaStatus + " Claude: " + ClaudeQuotaReader.Status, 11, "#9eb1ca"));
        if (localService.Settings.ReadAgentUsage && (codexQuota.NeedsRefresh(DateTimeOffset.UtcNow) || claudeQuota.NeedsRefresh(DateTimeOffset.UtcNow)))
            content.Children.Add(Text("A dash means a current reading is unavailable. Hover over a meter for details.", 11, "#9eb1ca"));
        return new Border { Child = content, Padding = new Thickness(14, 10), Background = Panel,
            CornerRadius = new CornerRadius(10), BorderBrush = Line, BorderThickness = new Thickness(1) };
    }

    Control SubscriptionQuotaRow(bool compact)
    {
        var now = DateTimeOffset.UtcNow;
        var enabled = localService.Settings.ReadAgentUsage;
        if (enabled && now >= nextQuotaRead && !quotaReadPending) _ = RefreshSubscriptionQuotas(now);

        var row = new UniformGrid { Columns = 4, MaxWidth = compact ? 272 : 560,
            HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center };
        const string openAi = "#57dfa7", anthropic = "#ff9e64";
        string claudeHint = ClaudeQuotaReader.Status;
        Add("ChatGPT", "5h", codexQuota.FiveHour, codexQuota.ObservedAt, openAi, "" + codexQuotaStatus + "");
        Add("ChatGPT", "Weekly", codexQuota.Weekly, codexQuota.ObservedAt, openAi, "" + codexQuotaStatus + "");
        Add("Claude", "5h", claudeQuota.FiveHour, claudeQuota.ObservedAt, anthropic, claudeHint);
        Add("Claude", "Weekly", claudeQuota.Weekly, claudeQuota.ObservedAt, anthropic, claudeHint);
        return row;

        void Add(string provider, string period, QuotaWindow? window, DateTimeOffset? observedAt, string numberColor, string hint)
        {
            var previous = window is null ? null : window with { ObservedAt = window.ObservedAt ?? observedAt };
            window = enabled ? ProviderQuota.ShownWindow(previous, now) : null;
            bool stale = ProviderQuota.IsLastReported(previous, now);
            observedAt = window?.ObservedAt;
            var number = window is null ? "—" : window.RemainingPercent.ToString(compact ? "0" : "0.#") + "%";
            double diameter = compact ? 36 : 62;
            var dial = new Grid { Width = diameter, Height = diameter, HorizontalAlignment = HorizontalAlignment.Center };
            dial.Children.Add(new QuotaRing(window?.UsedPercent, compact ? 2.5 : 4));
            var value = Text(number, compact ? 10.5 : 15, window is null ? "#8da1bd" : stale ? "#9eb1ca" : numberColor, true);
            value.HorizontalAlignment = HorizontalAlignment.Center;
            dial.Children.Add(value);
            var label = Text($"{provider} · {(compact && period == "Weekly" ? "7d" : period)}" + (!compact && stale && observedAt is { } asOf ? $"\nas of {asOf.ToLocalTime():h:mm tt}" : ""), compact ? 9 : 11, "#b7c8df");
            label.TextWrapping = TextWrapping.NoWrap;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new StackPanel { Spacing = compact ? 1 : 3, Children = { dial, label } };
            if (!compact) cell.Width = 120;
            var description = window is null
                ? $"{provider} {period} usage unavailable. " + (enabled ? (stale ? "The last reading is too old. " : "") + hint : "Enable local usage metadata in Settings.")
                : $"{provider} {period}: {number} left, {window.UsedPercent:0.#}% used" + (stale ? " (last reported; it changes only when you use " + provider + ")." : ".") +
                  (window.ResetsAt is { } reset ? $" Resets {reset.ToLocalTime():MMM d, h:mm tt}." : "") +
                  (observedAt is { } observed ? $" Reported {observed.ToLocalTime():MMM d, h:mm tt}." : "");
            ToolTip.SetTip(cell, description);
            AutomationProperties.SetName(cell, description);
            row.Children.Add(cell);
        }
    }
}

internal sealed class QuotaRing(double? usedPercent, double thickness) : Control
{
    internal static string StatusColor(double used) => used <= 50 ? "#4bd499" : used <= 75 ? "#f1bc63" : "#ff6f76";

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Min(center.X, center.Y) - thickness / 2 - 1;
        if (radius <= 0) return;

        var color = usedPercent is double used ? Color.Parse(StatusColor(used)) : Color.Parse("#526782");
        context.DrawEllipse(null, new Pen(new SolidColorBrush(color, 0.27), thickness), center, radius, radius);
        if (usedPercent is not > 0) return;

        var pen = new Pen(new SolidColorBrush(color), thickness, lineCap: PenLineCap.Round);
        if (usedPercent >= 100)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        double angle = usedPercent.Value * Math.Tau / 100;
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false);
            path.ArcTo(end, new Size(radius, radius), 0, usedPercent > 50, SweepDirection.Clockwise, true);
            path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);
    }
}

