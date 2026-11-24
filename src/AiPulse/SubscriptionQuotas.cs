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

    /// <summary>The last reading of a window that has not reset yet, however old. Claude usage is only
    /// reported when Claude Code is used, so an older value is still the best known one.</summary>
    internal static QuotaWindow? LastKnownWindow(QuotaWindow? window, DateTimeOffset now) =>
        window?.ObservedAt is { } at && at <= now.AddMinutes(5) &&
        (window.ResetsAt is { } reset ? reset > now : now - at < TimeSpan.FromHours(5))
            ? window
            : null;

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
    string claudeQuotaStatus = "Reading Claude Code usage.";
    string? claudeConnectMessage;

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
                ProviderQuota codex;
                try { codex = await CodexLiveQuota.Read(); codexQuotaStatus = "Live Codex account usage."; }
                catch (Exception e) { codex = new(null, null, null); codexQuotaStatus = e is OperationCanceledException ? "Codex usage request timed out. Retrying automatically." : "Codex usage unavailable. Check Codex installation and sign-in. Retrying automatically."; Paths.Log("codex-quota", e); }
                // Claude: AI Pulse never calls Anthropic. It reads only what Claude Code already has:
                // the status-line readings it recorded, and Claude Code's own saved usage reading.
                var statusLine = ReadClaudeQuota(now);
                var saved = ClaudeCodeUsage.ReadSaved();
                var bestClaude = ProviderQuota.Merge(now, statusLine, saved, claudeQuota);
                claudeQuotaStatus = ClaudeCodeUsage.Describe(bestClaude,
                    ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.ExecutablePath()).Connection, DateTimeOffset.UtcNow);
                return (codex, bestClaude);
            });
            (codexQuota, claudeQuota) = results;
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
        var content = new StackPanel { Spacing = 8, Children = { heading, row, Text(codexQuotaStatus + " Claude: " + claudeQuotaStatus, 11, "#9eb1ca") } };
        if (localService.Settings.ReadAgentUsage) content.Children.Add(ClaudeConnectRow());
        if (localService.Settings.ReadAgentUsage && (codexQuota.NeedsRefresh(DateTimeOffset.UtcNow) || claudeQuota.NeedsRefresh(DateTimeOffset.UtcNow)))
            content.Children.Add(Text("A dash means a current reading is unavailable. Hover over a meter for details.", 11, "#9eb1ca"));
        return new Border { Child = content, Padding = new Thickness(14, 10), Background = Panel,
            CornerRadius = new CornerRadius(10), BorderBrush = Line, BorderThickness = new Thickness(1) };
    }

    /// <summary>One-click setup of the Claude Code status line, the live source of Claude usage.</summary>
    Control ClaudeConnectRow()
    {
        var executable = ClaudeStatusLineSetup.ExecutablePath();
        var state = ClaudeStatusLineSetup.Inspect(executable);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        string text;
        Button? action = null;
        switch (state.Connection)
        {
            case ClaudeStatusLineSetup.Connection.NoClaudeCode:
                text = "Claude Code was not found on this computer. Claude usage appears once Claude Code is installed and used.";
                break;
            case ClaudeStatusLineSetup.Connection.Connected:
                text = "Live Claude usage is connected through Claude Code's status line" +
                       (state.ExistingCommand is null ? "." : "; your own status line still shows.");
                action = Btn("Disconnect Claude Code");
                action.Click += (_, _) => RunClaudeSetup(ClaudeStatusLineSetup.Disconnect,
                    "Disconnected. Your previous Claude Code status line is restored.");
                break;
            case ClaudeStatusLineSetup.Connection.ConnectedToOtherCopy:
                text = "Claude Code's status line points to another copy of AI Pulse.";
                action = Btn("Reconnect to this copy");
                break;
            default:
                text = "For live Claude usage, connect AI Pulse to Claude Code's status line. No sign-in or network access is needed" +
                       (state.ExistingCommand is null ? "." : "; your current status line keeps showing.");
                action = Btn("Connect Claude Code");
                break;
        }
        if (action is not null && state.Connection != ClaudeStatusLineSetup.Connection.Connected)
        {
            action.IsEnabled = executable is not null;
            action.Click += (_, _) => RunClaudeSetup(() => ClaudeStatusLineSetup.Connect(executable!),
                "Connected. Claude usage updates after your next message in Claude Code (already open sessions pick it up too).");
        }
        var label = Text(claudeConnectMessage ?? text, 11, "#9eb1ca");
        label.TextWrapping = TextWrapping.Wrap;
        label.MaxWidth = 460;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        if (action is not null) row.Children.Add(action);
        return row;
    }

    void RunClaudeSetup(Action change, string success)
    {
        try { change(); claudeConnectMessage = success; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            claudeConnectMessage = "Claude Code settings were not changed: " + e.Message;
            Paths.Log("claude-statusline-setup", e);
        }
        _ = RefreshSubscriptionQuotas(DateTimeOffset.UtcNow, manual: true);
        RenderBody();
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
        string claudeHint = claudeQuotaStatus;
        Add("ChatGPT", "5h", codexQuota.FiveHour, codexQuota.ObservedAt, openAi, "" + codexQuotaStatus + "");
        Add("ChatGPT", "Weekly", codexQuota.Weekly, codexQuota.ObservedAt, openAi, "" + codexQuotaStatus + "");
        Add("Claude", "5h", claudeQuota.FiveHour, claudeQuota.ObservedAt, anthropic, claudeHint, keepLastKnown: true);
        Add("Claude", "Weekly", claudeQuota.Weekly, claudeQuota.ObservedAt, anthropic, claudeHint, keepLastKnown: true);
        return row;

        void Add(string provider, string period, QuotaWindow? window, DateTimeOffset? observedAt, string numberColor, string hint, bool keepLastKnown = false)
        {
            var previous = window is null ? null : window with { ObservedAt = window.ObservedAt ?? observedAt };
            window = enabled ? ProviderQuota.FreshWindow(previous, now) : null;
            bool stale = ProviderQuota.IsLastReported(previous, now);
            bool lastKnown = false;
            if (window is null && enabled && keepLastKnown && ProviderQuota.LastKnownWindow(previous, now) is { } known)
            {
                window = known;
                lastKnown = true;
                numberColor = "#c9a27f"; // Muted: a correct but older reading.
            }
            observedAt = window?.ObservedAt;
            var number = window is null ? "—" : window.RemainingPercent.ToString(compact ? "0" : "0.#") + "%";
            double diameter = compact ? 36 : 62;
            var dial = new Grid { Width = diameter, Height = diameter, HorizontalAlignment = HorizontalAlignment.Center };
            dial.Children.Add(new QuotaRing(window?.UsedPercent, compact ? 2.5 : 4));
            var value = Text(number, compact ? 10.5 : 15, window is null ? "#8da1bd" : numberColor, true);
            value.HorizontalAlignment = HorizontalAlignment.Center;
            dial.Children.Add(value);
            var label = Text($"{provider} · {(compact && period == "Weekly" ? "7d" : period)}", compact ? 9 : 11, "#b7c8df");
            label.TextWrapping = TextWrapping.NoWrap;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new StackPanel { Spacing = compact ? 1 : 3, Children = { dial, label } };
            if (!compact) cell.Width = 120;
            var description = window is null
                ? $"{provider} {period} usage unavailable. " + (enabled ? (stale ? "The last reading is too old. " : "") + hint : "Enable local usage metadata in Settings.")
                : $"{provider} {period}: {number} left, {window.UsedPercent:0.#}% used." +
                  (window.ResetsAt is { } reset ? $" Resets {reset.ToLocalTime():MMM d, h:mm tt}." : "") +
                  (observedAt is { } observed ? $" Reported {observed.ToLocalTime():MMM d, h:mm tt}." : "") +
                  (lastKnown ? " No newer reading since then; it updates with your next Claude Code message." : "");
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

