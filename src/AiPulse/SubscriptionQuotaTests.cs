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
        // Claude Code's saved usage reading (cachedUsageUtilization in .claude.json).
        var fetchedAt = now.AddMinutes(-3);
        string SavedUsage(string savedAccount, string currentAccount) => $$$$"""
            {"numStartups":12,"oauthAccount":{"accountUuid":"{{{{currentAccount}}}}","emailAddress":"someone@example.com"},
             "projects":{"/work":{"history":[]}},
             "cachedUsageUtilization":{"fetchedAtMs":{{{{fetchedAt.ToUnixTimeMilliseconds()}}}},"accountUuid":"{{{{savedAccount}}}}",
               "utilization":{"five_hour":{"utilization":37.0,"resets_at":"{{{{now.AddHours(2):O}}}}"},
                              "seven_day":{"utilization":61.5,"resets_at":"{{{{now.AddDays(3):O}}}}"},
                              "seven_day_opus":null,"limits":[{"kind":"session","group":"session","percent":37}]}}}
            """;
        var savedReading = ClaudeCodeUsage.ParseSaved(SavedUsage("acct-1", "acct-1"));
        check(savedReading?.FiveHour is { UsedPercent: 37 } && savedReading.Weekly?.UsedPercent == 61.5 &&
              savedReading.FiveHour.ObservedAt == DateTimeOffset.FromUnixTimeMilliseconds(fetchedAt.ToUnixTimeMilliseconds()) &&
              savedReading.Weekly.ResetsAt == now.AddDays(3),
            "Claude usage is read from Claude Code's own saved reading in .claude.json, with its fetch time");
        check(ClaudeCodeUsage.ParseSaved(SavedUsage("acct-old", "acct-1")) is null &&
              ClaudeCodeUsage.ParseSaved("""{"numStartups":3}""") is null,
            "A saved Claude reading for another account, or no saved reading, is ignored");
        using (var usage = JsonDocument.Parse("""
            {"five_hour":{"utilization":0.0,"resets_at":null},"seven_day":{"utilization":41.0,"resets_at":"2026-11-16T09:00:00.123456+00:00"}}
            """))
        {
            var reading = ClaudeCodeUsage.ParseUsage(usage.RootElement, now);
            check(reading?.FiveHour is { UsedPercent: 0, ResetsAt: null } && reading.Weekly?.UsedPercent == 41 &&
                  ProviderQuota.Merge(now, reading).FiveHour?.UsedPercent == 0,
                "A 5-hour window that has not started counts as 0% used instead of a dash");
        }
        var olderStatusLine = new ProviderQuota(new QuotaWindow(20, now.AddHours(2), now.AddMinutes(-30)), null, now.AddMinutes(-30));
        var claudeMerged = ProviderQuota.Merge(now, olderStatusLine, savedReading);
        check(claudeMerged.FiveHour?.UsedPercent == 37 && claudeMerged.Weekly?.UsedPercent == 61.5,
            "The newest Claude reading wins, whichever Claude Code source it came from");
        var oldButValid = new QuotaWindow(55, now.AddHours(1), now.AddHours(-3));
        check(ProviderQuota.FreshWindow(oldButValid, now) is null && ProviderQuota.LastKnownWindow(oldButValid, now) == oldButValid &&
              ProviderQuota.LastKnownWindow(oldButValid, now.AddHours(2)) is null &&
              ProviderQuota.LastKnownWindow(new QuotaWindow(0, null, now.AddHours(-6)), now) is null,
            "Claude meters keep the last reading until its window resets instead of showing a dash");
        check(ClaudeCodeUsage.Describe(claudeMerged, ClaudeStatusLineSetup.Connection.Connected, now).Contains("3 min ago", StringComparison.Ordinal) &&
              ClaudeCodeUsage.Describe(new(null, null, null), ClaudeStatusLineSetup.Connection.NotConnected, now).Contains("Connect", StringComparison.Ordinal),
            "The Claude status text names the source age, or tells the user how to connect");

        var notStarted = ClaudeQuotaStatusLine.ParseStatusLine(
            """{"rate_limits":{"five_hour":{"used_percentage":0,"resets_at":null},"seven_day":{"used_percentage":10,"resets_at":""" + weeklyReset + "}}}",
            null, observed);
        check(notStarted?.FiveHour is { UsedPercentage: 0, ResetsAt: null } &&
              ClaudeQuotaStatusLine.Format(notStarted, observed).Contains("5h 100% left", StringComparison.Ordinal),
            "Claude status line accepts a not-yet-started window");
        ClaudeCodeSetup(root, check);

        check(QuotaRing.StatusColor(50) != QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75) == QuotaRing.StatusColor(50.1) &&
            QuotaRing.StatusColor(75.1) != QuotaRing.StatusColor(75),
            "Subscription usage colors change above 50 and 75 percent used");
    }

    /// <summary>Status-line setup against a temporary Claude Code folder, and the wrapped status line.</summary>
    static void ClaudeCodeSetup(string root, Action<bool, string> check)
    {
        var claudeDir = Path.Combine(root, "claude-setup", ".claude");
        var dataFile = Path.Combine(root, "claude-setup", "aipulse", "claude-quota.json");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        var saved = (Config: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Quota: Environment.GetEnvironmentVariable("AIPULSE_QUOTA_FILE"));
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeDir);
            Environment.SetEnvironmentVariable("AIPULSE_QUOTA_FILE", dataFile);
            var exe = OperatingSystem.IsWindows() ? @"C:\Program Files\AI Pulse\AiPulse.exe" : "/opt/ai-pulse/AiPulse";
            const string original = """
                {
                  // user comment
                  "model": "opus",
                  "permissions": { "allow": ["Bash(git status)"] },
                  "statusLine": { "type": "command", "command": "echo mine", "padding": 2 },
                }
                """;
            File.WriteAllText(settingsPath, original);
            check(ClaudeStatusLineSetup.Inspect(exe) is { Connection: ClaudeStatusLineSetup.Connection.NotConnected, ExistingCommand: "echo mine" },
                "Status-line setup sees the user's existing Claude Code status line");

            ClaudeStatusLineSetup.Connect(exe);
            using (var connected = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                var line = connected.RootElement.GetProperty("statusLine");
                check(line.GetProperty("command").GetString() == ClaudeStatusLineSetup.CommandFor(exe) &&
                      ClaudeStatusLineSetup.CommandFor(exe).Contains("/AiPulse", StringComparison.Ordinal) &&
                      !ClaudeStatusLineSetup.CommandFor(exe).Contains('\\') &&
                      line.GetProperty("padding").GetInt32() == 2 && connected.RootElement.GetProperty("model").GetString() == "opus" &&
                      connected.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString() == "Bash(git status)" &&
                      File.ReadAllText(settingsPath + ".before-aipulse") == original &&
                      ClaudeStatusLineSetup.WrappedCommand() == "echo mine" &&
                      ClaudeStatusLineSetup.Inspect(exe) is { Connection: ClaudeStatusLineSetup.Connection.Connected, ExistingCommand: "echo mine" },
                    "Connecting points Claude Code's status line at AI Pulse, keeps every other setting, backs up the file and wraps the old command");
            }
            ClaudeStatusLineSetup.Connect(exe);
            check(ClaudeStatusLineSetup.WrappedCommand() == "echo mine" &&
                  ClaudeStatusLineSetup.Inspect(exe + "-moved").Connection == ClaudeStatusLineSetup.Connection.ConnectedToOtherCopy,
                "Connecting twice never wraps AI Pulse in itself, and a moved copy is detected");

            ClaudeStatusLineSetup.Disconnect();
            using (var restored = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                check(restored.RootElement.GetProperty("statusLine").GetProperty("command").GetString() == "echo mine" &&
                      restored.RootElement.GetProperty("statusLine").GetProperty("padding").GetInt32() == 2 &&
                      ClaudeStatusLineSetup.WrappedCommand() is null,
                    "Disconnecting restores the user's own status line");

            File.WriteAllText(settingsPath, """{"model":"opus"}""");
            ClaudeStatusLineSetup.Connect(exe);
            var connectedFresh = ClaudeStatusLineSetup.Inspect(exe);
            ClaudeStatusLineSetup.Disconnect();
            using (var removed = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                check(connectedFresh is { Connection: ClaudeStatusLineSetup.Connection.Connected, ExistingCommand: null } &&
                      !removed.RootElement.TryGetProperty("statusLine", out _) && removed.RootElement.GetProperty("model").GetString() == "opus",
                    "Without a previous status line, connecting adds one and disconnecting removes it");

            File.WriteAllText(settingsPath, "{ not json");
            bool refused = false;
            try { ClaudeStatusLineSetup.Connect(exe); } catch (InvalidOperationException) { refused = true; }
            check(refused && File.ReadAllText(settingsPath) == "{ not json",
                "A damaged Claude Code settings file is never overwritten");

            check(ClaudeStatusLineSetup.RunCommand("echo chained-ok", "{}", null, TimeSpan.FromSeconds(20)) == "chained-ok" &&
                  (OperatingSystem.IsWindows() ||
                   ClaudeStatusLineSetup.RunCommand("cat", "{\"model\":\"\u00e9\"}", null, TimeSpan.FromSeconds(20)) == "{\"model\":\"\u00e9\"}"),
                "A wrapped status line runs through a shell, gets Claude Code's input and its output is passed on");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", saved.Config);
            Environment.SetEnvironmentVariable("AIPULSE_QUOTA_FILE", saved.Quota);
        }
    }
}
