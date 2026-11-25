using System.Globalization;
using System.Text.Json;

namespace AiPulse;

/// <summary>Accepts Claude Code's status-line JSON and keeps only quota percentages and reset times.</summary>
internal static class ClaudeQuotaStatusLine
{
    // ResetsAt is null for a window that has not started yet (no usage in it so far).
    internal sealed record Window(double UsedPercentage, long? ResetsAt, long ObservedAt);
    internal sealed record Snapshot(long ObservedAt, Window? FiveHour, Window? SevenDay);

    internal static string CachePath()
    {
        var configured = Environment.GetEnvironmentVariable("AIPULSE_QUOTA_FILE");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Paths.DefaultRoot, "claude-quota.json")
            : Path.GetFullPath(configured);
    }

    // Written on every status-line call. It proves terminal Claude Code is active, which is when
    // Claude Code renews its own sign-in and AI Pulse must not touch it.
    internal static string HeartbeatPath() => Path.Combine(Path.GetDirectoryName(CachePath())!, "claude-statusline-seen");

    internal static DateTimeOffset? LastSeen()
    {
        try
        {
            var path = HeartbeatPath();
            return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var seconds) && ValidUnixTime(seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static int Run(string[]? args = null)
    {
        string input = "";
        Snapshot? current = null;
        try
        {
            input = Console.In.ReadToEnd();
            using var document = JsonDocument.Parse(input);
            var path = CachePath();
            current = ReadCache(path);
            var observed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Claude Code can invoke a status-line command during startup before limits are known.
            // Keep the last useful sample in that case and continue displaying it.
            var updated = ParseStatusLine(document.RootElement, current, observed);
            if (!ReferenceEquals(updated, current))
            {
                current = updated;
                if (current is not null) WriteCache(path, current);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatPath())!);
            File.WriteAllText(HeartbeatPath(), observed.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // This command runs in Claude Code's status-line pipeline. Never surface parsing or
            // disk errors as a prompt or diagnostic that could disrupt the user's session.
        }
        try
        {
            // A user's own status line keeps working: AI Pulse records the limits, then hands the
            // same input to their original command and prints whatever it prints.
            if (args?.Contains(ChainFlag) == true && ClaudeStatusLineSetup.ReadChained() is { } chained &&
                RunChained(chained, input) is { } output)
            {
                Console.Out.Write(output);
                return 0;
            }
            Console.Out.WriteLine(Format(current));
        }
        catch { Console.Out.WriteLine("Claude usage unavailable"); }
        return 0;
    }

    internal const string ChainFlag = "--chain";

    /// <summary>Runs the user's original status-line command with the same stdin. Null if it could not run.</summary>
    internal static string? RunChained(string command, string input, TimeSpan? timeout = null)
    {
        // Claude Code runs status lines through bash (Git Bash on Windows), so run the original the same way.
        var gitBash = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        var shell = OperatingSystem.IsWindows() ? (string.IsNullOrWhiteSpace(gitBash) ? "bash" : gitBash) : "/bin/sh";
        var start = new System.Diagnostics.ProcessStartInfo(shell)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(input); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(10)))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            return stdout.GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    internal static Snapshot? ParseCache(string json, long? now = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryLong(root, "observed_at", out var observedAt) ||
            !ValidUnixTime(observedAt) || !root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return null;
        var time = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fiveHour = ReadWindow(limits, "five_hour", observedAt, time);
        var sevenDay = ReadWindow(limits, "seven_day", observedAt, time);
        return fiveHour is null && sevenDay is null ? null : new(observedAt, fiveHour, sevenDay);
    }

    internal static Snapshot? ParseStatusLine(string json, Snapshot? previous, long observedAt)
    {
        using var document = JsonDocument.Parse(json);
        return ParseStatusLine(document.RootElement, previous, observedAt);
    }

    private static Snapshot? ParseStatusLine(JsonElement root, Snapshot? previous, long observedAt)
    {
        var usablePrevious = Expire(previous, observedAt);
        if (!ValidUnixTime(observedAt)) return usablePrevious;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return usablePrevious;
        var fiveHour = ReadWindow(limits, "five_hour", observedAt, observedAt);
        var sevenDay = ReadWindow(limits, "seven_day", observedAt, observedAt);
        if (fiveHour is null && sevenDay is null) return usablePrevious;
        return new(observedAt, fiveHour ?? usablePrevious?.FiveHour, sevenDay ?? usablePrevious?.SevenDay);
    }

    internal static string Format(Snapshot? snapshot, long? now = null)
    {
        if (snapshot is null) return "Claude usage unavailable";
        var time = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        snapshot = Expire(snapshot, time);
        if (snapshot is null) return "Claude usage unavailable";
        var parts = new List<string>(2);
        if (snapshot.FiveHour is { } fiveHour) parts.Add(FormatWindow("5h", fiveHour, time));
        if (snapshot.SevenDay is { } sevenDay) parts.Add(FormatWindow("7d", sevenDay, time));
        return parts.Count == 0 ? "Claude usage unavailable" : "Claude " + string.Join(" · ", parts);
    }

    private static string FormatWindow(string label, Window window, long now)
    {
        var remaining = Math.Clamp((int)Math.Round(100 - window.UsedPercentage, MidpointRounding.AwayFromZero), 0, 100);
        var reset = window.ResetsAt is { } resetAt ? $" · resets {FormatEta(resetAt - now)}" : "";
        return $"{label} {remaining}% left{reset}";
    }

    private static string FormatEta(long seconds)
    {
        if (seconds <= 0) return "now";
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1) return $"in {(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"in {(int)duration.TotalHours}h {duration.Minutes:D2}m";
        return $"in {Math.Max(1, duration.Minutes)}m";
    }

    private static Window? ReadWindow(JsonElement limits, string name, long observedAt, long now)
    {
        if (!limits.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object ||
            !TryDouble(element, "used_percentage", out var percentage) || percentage < 0)
            return null;
        long? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var resetValue) && resetValue.ValueKind != JsonValueKind.Null)
        {
            if (!TryResetTime(element, out var parsedReset) || parsedReset <= now) return null;
            resetsAt = parsedReset;
        }
        var windowObservedAt = element.TryGetProperty("observed_at", out var observed) && observed.ValueKind == JsonValueKind.Number &&
                               observed.TryGetInt64(out var ownObserved) && ValidUnixTime(ownObserved)
            ? ownObserved : observedAt;
        return new(Math.Min(100, percentage), resetsAt, windowObservedAt);
    }

    private static bool TryResetTime(JsonElement element, out long resetsAt)
    {
        resetsAt = 0;
        if (!element.TryGetProperty("resets_at", out var reset)) return false;
        if (reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out resetsAt)) return ValidUnixTime(resetsAt);
        if (reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            resetsAt = parsed.ToUnixTimeSeconds();
            return ValidUnixTime(resetsAt);
        }
        return false;
    }

    private static bool ValidUnixTime(long value) => value > 0 && value <= DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private static Snapshot? Expire(Snapshot? snapshot, long now)
    {
        if (snapshot is null) return null;
        var fiveHour = snapshot.FiveHour is { } five && (five.ResetsAt is null || five.ResetsAt > now) ? five : null;
        var sevenDay = snapshot.SevenDay is { } seven && (seven.ResetsAt is null || seven.ResetsAt > now) ? seven : null;
        return fiveHour is null && sevenDay is null ? null :
            ReferenceEquals(fiveHour, snapshot.FiveHour) && ReferenceEquals(sevenDay, snapshot.SevenDay)
                ? snapshot : new(snapshot.ObservedAt, fiveHour, sevenDay);
    }

    private static bool TryDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    /// <summary>Saves a live usage-endpoint reading so it survives restarts and merges with status-line readings.</summary>
    internal static void Store(ProviderQuota reading)
    {
        Window? Convert(QuotaWindow? window, DateTimeOffset? fallbackObserved)
        {
            if (window is null || (window.ObservedAt ?? fallbackObserved) is not { } observed) return null;
            return new(window.UsedPercent, window.ResetsAt?.ToUnixTimeSeconds(), observed.ToUnixTimeSeconds());
        }
        var fiveHour = Convert(reading.FiveHour, reading.ObservedAt);
        var sevenDay = Convert(reading.Weekly, reading.ObservedAt);
        if (fiveHour is null && sevenDay is null) return;
        var path = CachePath();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var previous = Expire(ReadCache(path), now);
        Window? Newer(Window? incoming, Window? existing) =>
            incoming is null ? existing : existing is null || incoming.ObservedAt >= existing.ObservedAt ? incoming : existing;
        fiveHour = Newer(fiveHour, previous?.FiveHour);
        sevenDay = Newer(sevenDay, previous?.SevenDay);
        WriteCache(path, new(Math.Max(fiveHour?.ObservedAt ?? 0, sevenDay?.ObservedAt ?? 0), fiveHour, sevenDay));
    }

    internal static Snapshot? ReadCache(string path)
    {
        try { return File.Exists(path) ? ParseCache(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static void WriteCache(string path, Snapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Cache path needs a directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("observed_at", snapshot.ObservedAt);
                    writer.WritePropertyName("rate_limits"); writer.WriteStartObject();
                    WriteWindow(writer, "five_hour", snapshot.FiveHour);
                    WriteWindow(writer, "seven_day", snapshot.SevenDay);
                    writer.WriteEndObject(); writer.WriteEndObject(); writer.Flush();
                }
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void WriteWindow(Utf8JsonWriter writer, string name, Window? window)
    {
        if (window is null) return;
        writer.WritePropertyName(name); writer.WriteStartObject();
        writer.WriteNumber("used_percentage", window.UsedPercentage);
        if (window.ResetsAt is { } resetsAt) writer.WriteNumber("resets_at", resetsAt);
        else writer.WriteNull("resets_at");
        writer.WriteNumber("observed_at", window.ObservedAt);
        writer.WriteEndObject();
    }
}
