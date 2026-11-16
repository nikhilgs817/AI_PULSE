using System.Net;
using System.Net.Http.Headers;
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
        check(ClaudeQuotaReader.RateLimitDelay(1, null) == TimeSpan.FromMinutes(5) &&
              ClaudeQuotaReader.RateLimitDelay(3, null) == TimeSpan.FromMinutes(20) &&
              ClaudeQuotaReader.RateLimitDelay(9, null) == TimeSpan.FromHours(1) &&
              ClaudeQuotaReader.RateLimitDelay(1, TimeSpan.Zero) == TimeSpan.FromMinutes(5) &&
              ClaudeQuotaReader.RateLimitDelay(1, TimeSpan.FromSeconds(10)) == TimeSpan.FromMinutes(1) &&
              ClaudeQuotaReader.RateLimitDelay(1, TimeSpan.FromMinutes(12)) == TimeSpan.FromMinutes(12) &&
              ClaudeQuotaReader.RateLimitDelay(1, TimeSpan.FromHours(9)) == TimeSpan.FromHours(1),
            "After HTTP 429 Claude usage waits for Retry-After (ignoring 0) or 5, 10, 20 minutes, capped at one hour");
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
        using (var body = JsonDocument.Parse(ClaudeQuotaReader.RenewalBody(parsedCredentials!)))
            check(body.RootElement.GetProperty("grant_type").GetString() == "refresh_token" &&
                  body.RootElement.GetProperty("refresh_token").GetString() == "old-refresh" &&
                  body.RootElement.GetProperty("client_id").GetString() == "9d1c250a-e61b-44d9-88ed-5944d1962f5e" &&
                  body.RootElement.GetProperty("scope").GetString() == "user:inference",
                "Sign-in renewal sends Claude Code's JSON body, including the stored scopes");
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

        RequestFlow(root, check);

        check(QuotaRing.StatusColor(50) != QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75) == QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75.1) != QuotaRing.StatusColor(75),
            "Subscription usage colors change above 50 and 75 percent used");
    }

    /// <summary>Fake Anthropic server: answers each request from a queue and records what was sent.</summary>
    sealed class FakeAnthropic : HttpMessageHandler
    {
        internal readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> Answers = new();
        internal readonly List<(string Url, string? Body, string? Agent)> Seen = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.RequestUri!.ToString(), body, request.Headers.UserAgent.ToString()));
            return Answers.Count == 0 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Answers.Dequeue()(request);
        }

        internal static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        internal static HttpResponseMessage Limited(TimeSpan retryAfter)
        {
            var limited = Json(HttpStatusCode.TooManyRequests, """{"error":{"type":"rate_limit_error"}}""");
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            return limited;
        }
    }

    /// <summary>Runs the real reader end to end against the fake server.</summary>
    static void RequestFlow(string root, Action<bool, string> check)
    {
        var claudeDir = Path.Combine(root, "claude-flow", ".claude");
        var quotaFile = Path.Combine(root, "claude-flow", "aipulse", "claude-quota.json");
        Directory.CreateDirectory(claudeDir);
        var credentialsPath = Path.Combine(claudeDir, ".credentials.json");
        var saved = (Config: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Quota: Environment.GetEnvironmentVariable("AIPULSE_QUOTA_FILE"),
            Token: Environment.GetEnvironmentVariable("AIPULSE_CLAUDE_TOKEN_URL"));
        var fake = new FakeAnthropic();
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeDir);
            Environment.SetEnvironmentVariable("AIPULSE_QUOTA_FILE", quotaFile);
            Environment.SetEnvironmentVariable("AIPULSE_CLAUDE_TOKEN_URL", null);
            var start = DateTimeOffset.UtcNow;
            File.WriteAllText(credentialsPath, $$$"""
                {"claudeAiOauth":{"accessToken":"expired-access","refreshToken":"refresh-1","expiresAt":{{{start.AddHours(-3).ToUnixTimeMilliseconds()}}},
                 "scopes":["user:inference","user:profile"],"subscriptionType":"max"}}
                """);

            // 1. Expired sign-in; Anthropic throttles the renewal and sends a misleading Retry-After of 0.
            ClaudeQuotaReader.UseForTests(fake);
            fake.Answers.Enqueue(_ => FakeAnthropic.Limited(TimeSpan.Zero));
            var first = ClaudeQuotaReader.Read(start).GetAwaiter().GetResult();
            check(first is null && fake.Seen.Count == 1 && fake.Seen[0].Url == "https://platform.claude.com/v1/oauth/token" &&
                  fake.Seen[0].Body!.Contains("\"scope\":\"user:inference user:profile\"", StringComparison.Ordinal) &&
                  fake.Seen[0].Agent!.StartsWith("claude-cli/", StringComparison.Ordinal) &&
                  ClaudeQuotaReader.Status.Contains("limiting sign-in renewal", StringComparison.Ordinal) &&
                  ClaudeQuotaReader.NextAttempt >= start.AddMinutes(5).AddSeconds(-1),
                "An expired Claude sign-in is renewed at platform.claude.com with Claude Code's body; a 429 with Retry-After 0 waits 5 minutes");

            // 2. Neither the timer, a manual refresh nor an app restart may call again during the wait.
            ClaudeQuotaReader.Read(start.AddMinutes(1)).GetAwaiter().GetResult();
            ClaudeQuotaReader.Read(start.AddMinutes(2), manual: true).GetAwaiter().GetResult();
            ClaudeQuotaReader.UseForTests(fake); // simulates a restart: the wait must come back from disk
            ClaudeQuotaReader.Read(start.AddMinutes(3), manual: true).GetAwaiter().GetResult();
            check(fake.Seen.Count == 1 && ClaudeQuotaReader.WaitingForProvider(start.AddMinutes(3)),
                "During Anthropic's wait no request is sent, even after a manual refresh or an app restart");

            // 3. After the wait: renewal succeeds, the file is updated, usage is read with the new token.
            fake.Answers.Enqueue(_ => FakeAnthropic.Json(HttpStatusCode.OK,
                """{"access_token":"fresh-access","refresh_token":"refresh-2","expires_in":28800,"scope":"user:inference user:profile"}"""));
            fake.Answers.Enqueue(request => request.Headers.Authorization?.Parameter == "fresh-access"
                ? FakeAnthropic.Json(HttpStatusCode.OK, $$$"""
                    {"five_hour":{"utilization":0.0,"resets_at":null},"seven_day":{"utilization":22.0,"resets_at":"{{{start.AddDays(2):O}}}"}}
                    """)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var reading = ClaudeQuotaReader.Read(start.AddMinutes(6)).GetAwaiter().GetResult();
            var storedText = File.ReadAllText(credentialsPath);
            var stored = ClaudeQuotaReader.ReadCredentials(storedText);
            check(reading?.FiveHour?.UsedPercent == 0 && reading.Weekly?.UsedPercent == 22 &&
                  fake.Seen.Count == 3 && fake.Seen[2].Url == "https://api.anthropic.com/api/oauth/usage?skip_spend=1" &&
                  stored is { AccessToken: "fresh-access", RefreshToken: "refresh-2" } &&
                  storedText.Contains("\"subscriptionType\":\"max\"", StringComparison.Ordinal) &&
                  ClaudeQuotaReader.Status == "Live account usage." && !ClaudeQuotaReader.WaitingForProvider(start.AddMinutes(6)),
                "After the wait the sign-in is renewed, saved without losing other fields, and live usage is shown");

            // 4. A usage-endpoint 429 is honoured for its full Retry-After, without touching the sign-in.
            fake.Answers.Enqueue(_ => FakeAnthropic.Limited(TimeSpan.FromMinutes(17)));
            var limitedAt = start.AddMinutes(12);
            ClaudeQuotaReader.Read(limitedAt).GetAwaiter().GetResult();
            ClaudeQuotaReader.Read(limitedAt.AddMinutes(16), manual: true).GetAwaiter().GetResult();
            check(fake.Seen.Count == 4 && fake.Seen[3].Url.StartsWith("https://api.anthropic.com/", StringComparison.Ordinal) &&
                  ClaudeQuotaReader.NextAttempt.ToUnixTimeSeconds() == limitedAt.AddMinutes(17).ToUnixTimeSeconds() &&
                  ClaudeQuotaReader.Status.Contains("limiting usage requests", StringComparison.Ordinal),
                "A usage-endpoint 429 is honoured for its full Retry-After without renewing the sign-in");

            // 5. A refused refresh token is never sent again.
            File.WriteAllText(credentialsPath, """{"claudeAiOauth":{"accessToken":"dead-access","refreshToken":"refresh-dead","expiresAt":1}}""");
            fake.Answers.Enqueue(_ => FakeAnthropic.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
            var later = limitedAt.AddMinutes(18);
            ClaudeQuotaReader.Read(later).GetAwaiter().GetResult();
            ClaudeQuotaReader.Read(later.AddHours(1)).GetAwaiter().GetResult();
            check(fake.Seen.Count == 5 && ClaudeQuotaReader.Status.Contains("/login", StringComparison.Ordinal),
                "A refused refresh token is not retried and the user is told to sign in again");
        }
        finally
        {
            ClaudeQuotaReader.UseForTests(new HttpClientHandler { AllowAutoRedirect = false });
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", saved.Config);
            Environment.SetEnvironmentVariable("AIPULSE_QUOTA_FILE", saved.Quota);
            Environment.SetEnvironmentVariable("AIPULSE_CLAUDE_TOKEN_URL", saved.Token);
        }
    }
}
