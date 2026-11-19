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
            ProviderQuota.FreshWindow(freshShort with { ObservedAt = now, ResetsAt = now },now) is null &&
            ProviderQuota.ShownWindow(oldWeekly,now) is not null && ProviderQuota.ShownWindow(oldWeekly with { ResetsAt = now },now) is null,
            "Both dashboard views drop reset windows; older readings stay visible as last reported until reset");

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
        // Usage endpoint: a window that has not started reports utilization 0 with resets_at null.
        using (var usage = JsonDocument.Parse("""
            {"five_hour":{"utilization":0.0,"resets_at":null},
             "seven_day":{"utilization":41.0,"resets_at":"2026-11-16T09:00:00.123456+00:00"},
             "seven_day_opus":null}
            """))
        {
            var reading = ClaudeQuotaReader.Parse(usage.RootElement, now);
            check(reading?.FiveHour is { UsedPercent: 0, ResetsAt: null } && reading.Weekly?.UsedPercent == 41 &&
                  ProviderQuota.FreshWindow(reading.FiveHour, now) is not null && !reading.NeedsRefresh(now),
                "Claude usage with a not-yet-started 5-hour window shows 100% left instead of a dash and does not force re-polling");
            check(ProviderQuota.Merge(now, reading).FiveHour?.UsedPercent == 0,
                "Merging keeps Claude windows that have no reset time");
            check(ProviderQuota.FreshWindow(reading.FiveHour, now.AddMinutes(16)) is null,
                "A window without a reset time still goes stale after 15 minutes");
        }

        check(ClaudeQuotaReader.BackoffDelay(1, null) == TimeSpan.FromMinutes(2) &&
              ClaudeQuotaReader.BackoffDelay(3, null) == TimeSpan.FromMinutes(8) &&
              ClaudeQuotaReader.BackoffDelay(10, null) == ClaudeQuotaReader.MaxBackoff &&
              ClaudeQuotaReader.BackoffDelay(1, TimeSpan.FromMinutes(20)) == TimeSpan.FromMinutes(20) &&
              ClaudeQuotaReader.BackoffDelay(1, TimeSpan.FromSeconds(5)) == TimeSpan.FromMinutes(2),
            "Claude usage back-off grows exponentially, is capped, and honours a longer Retry-After");
        check(ClaudeQuotaReader.RetryAfter(new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90)), now) == TimeSpan.FromSeconds(90) &&
              ClaudeQuotaReader.RetryAfter(new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(3)), now) == TimeSpan.FromMinutes(3) &&
              ClaudeQuotaReader.RetryAfter(null, now) is null,
            "Retry-After is read as seconds or as a date");

        const string credentials = """
            {"claudeAiOauth":{"accessToken":"old-access","refreshToken":"old-refresh","expiresAt":1700000000000,
             "scopes":["user:inference"],"subscriptionType":"max"},"otherTool":{"keep":true}}
            """;
        var parsedCredentials = ClaudeQuotaReader.ReadCredentials(credentials);
        check(parsedCredentials is { AccessToken: "old-access", RefreshToken: "old-refresh" } &&
              parsedCredentials.ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
            "Claude Code credentials are read with their expiry");
        using (var tokenResponse = JsonDocument.Parse("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":28800,"scope":"user:inference user:profile"}"""))
        {
            var renewed = ClaudeQuotaReader.ApplyRenewal(credentials, tokenResponse.RootElement, now);
            var after = ClaudeQuotaReader.ReadCredentials(renewed);
            using var renewedDocument = JsonDocument.Parse(renewed);
            var oauth = renewedDocument.RootElement.GetProperty("claudeAiOauth");
            check(after is { AccessToken: "new-access", RefreshToken: "new-refresh" } && after.ExpiresAt == now.AddHours(8) &&
                  oauth.GetProperty("subscriptionType").GetString() == "max" && oauth.GetProperty("scopes").GetArrayLength() == 2 &&
                  renewedDocument.RootElement.GetProperty("otherTool").GetProperty("keep").GetBoolean(),
                "Token renewal updates only the token fields and keeps every other credential field");
        }
        using (var noRotation = JsonDocument.Parse("""{"access_token":"new-access","expires_in":3600}"""))
            check(ClaudeQuotaReader.ReadCredentials(ClaudeQuotaReader.ApplyRenewal(credentials, noRotation.RootElement, now))?.RefreshToken == "old-refresh",
                "Token renewal keeps the existing refresh token when the provider does not rotate it");

        var notStarted = ClaudeQuotaStatusLine.ParseStatusLine(
            """{"rate_limits":{"five_hour":{"used_percentage":0,"resets_at":null},"seven_day":{"used_percentage":10,"resets_at":""" + weeklyReset + "}}}",
            null, observed);
        check(notStarted?.FiveHour is { UsedPercentage: 0, ResetsAt: null } &&
              ClaudeQuotaStatusLine.Format(notStarted, observed).Contains("5h 100% left", StringComparison.Ordinal),
            "Claude status line accepts a not-yet-started window");
        var cacheFile = Path.Combine(root, "claude-quota-cache.json");
        var previousQuotaFile = Environment.GetEnvironmentVariable("AIPULSE_QUOTA_FILE");
        try
        {
            Environment.SetEnvironmentVariable("AIPULSE_QUOTA_FILE", cacheFile);
            var live = DateTimeOffset.UtcNow;
            ClaudeQuotaStatusLine.Store(new(new QuotaWindow(0, null, live), new QuotaWindow(12, live.AddDays(2), live), live));
            var stored = ClaudeQuotaStatusLine.ReadCache(cacheFile);
            check(stored?.FiveHour is { UsedPercentage: 0, ResetsAt: null } && stored.SevenDay?.UsedPercentage == 12,
                "Live Claude usage readings are cached so a restart does not need another provider call");
        }
        finally { Environment.SetEnvironmentVariable("AIPULSE_QUOTA_FILE", previousQuotaFile); }

        check(QuotaRing.StatusColor(50) !=QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75) == QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75.1) != QuotaRing.StatusColor(75),
            "Subscription usage colors change above 50 and 75 percent used");
    }
}
