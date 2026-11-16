using System.Text.Json;

namespace AiPulse;

internal static class SubscriptionQuotaTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.Zero);
        var fiveHourReset = now.AddHours(2).ToUnixTimeSeconds();
        var weeklyReset = now.AddDays(3).ToUnixTimeSeconds();
        string CodexRecord(string bucket, DateTimeOffset timestamp, double used) => JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = new
                {
                    limit_id = bucket,
                    primary = new { window_minutes = 10080, used_percent = used, resets_at = weeklyReset },
                    secondary = new { window_minutes = 300, used_percent = 25.0, resets_at = fiveHourReset }
                }
            }
        });

        string codexHome = Path.Combine(root, "codex-quota");
        string sessions = Path.Combine(codexHome, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "rollout-quota.jsonl"),
            CodexRecord("codex", now.AddMinutes(-2), 68) + "\n" +
            CodexRecord("codex", now.AddMinutes(-1), 72) + "\n" +
            CodexRecord("premium", now, 98) + "\n" + "{\"rate_limits\":");
        var codex = CodexQuotaReader.Read(codexHome, now);
        check(codex.FiveHour?.RemainingPercent == 75 && codex.Weekly?.RemainingPercent == 28,
            "Codex quota reads the latest matching bucket and identifies windows by duration");
        codex = CodexQuotaReader.Read(codexHome, now.AddHours(2));
        check(codex.FiveHour is null && codex.Weekly?.RemainingPercent == 28,
            "Codex quota expires only the window that has reset");
        var oldWeekly = new QuotaWindow(72, now.AddDays(3), now.AddHours(-4));
        var freshShort = new QuotaWindow(20, now.AddHours(2), now.AddSeconds(-30));
        var merged = ProviderQuota.Merge(now, new(freshShort, null, now), new(null, oldWeekly, now.AddHours(-4)));
        check(merged.FiveHour == freshShort && merged.Weekly == oldWeekly &&
            ProviderQuota.IsLastReported(merged.Weekly, now) && !ProviderQuota.IsLastReported(merged.FiveHour, now),
            "A partial update keeps the older unexpired window with its own last-reported marker");
        check(ProviderQuota.Merge(now.AddHours(3), merged).FiveHour is null && ProviderQuota.Merge(now.AddHours(3), merged).Weekly == oldWeekly,
            "A failed refresh preserves unexpired readings and clears only windows past reset");
        check(merged.NeedsRefresh(now) && !new ProviderQuota(freshShort,freshShort,now).NeedsRefresh(now),
            "Subscription polling detects missing or stale windows and keeps normal cadence for fresh readings");
        check(ProviderQuota.FreshWindow(oldWeekly,now) is null &&
            ProviderQuota.FreshWindow(freshShort,now) == freshShort &&
            ProviderQuota.FreshWindow(freshShort with { ObservedAt = now, ResetsAt = now },now) is null,
            "Both dashboard views hide stale or reset quota values even after a failed refresh");

        long observed = now.ToUnixTimeSeconds();
        var first = ClaudeQuotaStatusLine.ParseStatusLine(
            JsonSerializer.Serialize(new { rate_limits = new { five_hour = new { used_percentage = 80.5, resets_at = fiveHourReset } } }),
            null, observed);
        var second = ClaudeQuotaStatusLine.ParseStatusLine(
            JsonSerializer.Serialize(new { rate_limits = new { seven_day = new { used_percentage = 34, resets_at = weeklyReset } } }),
            first, observed + 60);
        check(second?.FiveHour?.ObservedAt == observed && second.SevenDay?.ObservedAt == observed + 60 &&
            ClaudeQuotaStatusLine.Format(second, observed + 60).Contains("7d 66% left", StringComparison.Ordinal),
            "Claude quota updates each limit independently and preserves the original observation time");
        check(ClaudeQuotaStatusLine.ParseStatusLine("{}", second, observed + 120) == second &&
            !ClaudeQuotaStatusLine.Format(second, fiveHourReset).Contains("5h", StringComparison.Ordinal),
            "Claude startup data preserves existing usage and expired values disappear");
        check(QuotaRing.StatusColor(50) != QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75) == QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75.1) != QuotaRing.StatusColor(75),
            "Subscription usage colors change above 50 and 75 percent used");
    }
}
