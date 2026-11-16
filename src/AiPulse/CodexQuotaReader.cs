using System.Globalization;
using System.Text.Json;

namespace AiPulse;

internal static class CodexQuotaReader
{
    const int MaxSessions = 8;
    const int TailBytes = 1024 * 1024;

    internal static ProviderQuota Read(string codexHome, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) return new(null, null, null);

        var snapshots = new List<ProviderQuota>();
        byte[]? buffer = null;
        try
        {
            var sessions = new DirectoryInfo(Path.Combine(codexHome, "sessions"));
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var recent = sessions.EnumerateFiles("rollout-*.jsonl", options)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxSessions);
            foreach (var file in recent)
            {
                var snapshot = ReadTail(file.FullName, buffer ??= new byte[TailBytes], now);
                if (snapshot is not null) snapshots.Add(snapshot);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }

        return ProviderQuota.Merge(now, snapshots.ToArray());
    }

    static ProviderQuota? ReadTail(string path, byte[] bytes, DateTimeOffset now)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - TailBytes);
            stream.Position = start;
            int count = stream.ReadAtLeast(bytes, bytes.Length, false);

            var contents = bytes.AsMemory(0, count);
            int first = start == 0 ? 0 : contents.Span.IndexOf((byte)'\n') + 1;
            if (start != 0 && first == 0) return null;

            // A writer can be appending the last record. The parser skips incomplete JSON.
            ProviderQuota? latest=null;
            while (count > first)
            {
                int previous = contents.Span[first..(count - 1)].LastIndexOf((byte)'\n');
                int lineStart = previous < 0 ? first : first + previous + 1;
                var snapshot = Parse(contents[lineStart..count], now);
                if (snapshot is not null)
                {
                    latest=ProviderQuota.Merge(now,latest,snapshot);
                    if(latest.FiveHour is not null&&latest.Weekly is not null)return latest;
                }
                count = lineStart;
            }
            return latest;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    internal static ProviderQuota? Parse(ReadOnlyMemory<byte> line, DateTimeOffset now)
    {
        if (line.Span.IndexOf("\"rate_limits\""u8) < 0) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!IsString(root, "type", "event_msg") ||
                !root.TryGetProperty("payload", out var payload) ||
                !IsString(payload, "type", "token_count") ||
                !payload.TryGetProperty("rate_limits", out var limits) ||
                !IsString(limits, "limit_id", "codex") ||
                !root.TryGetProperty("timestamp", out var timestamp) ||
                timestamp.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var observed) ||
                observed > now.AddMinutes(5))
                return null;

            QuotaWindow? fiveHour = null;
            QuotaWindow? weekly = null;
            foreach (string field in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("window_minutes", out var duration) ||
                    duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out int minutes) ||
                    !value.TryGetProperty("used_percent", out var usage) ||
                    usage.ValueKind != JsonValueKind.Number || !usage.TryGetDouble(out double usedPercent) ||
                    !double.IsFinite(usedPercent) || usedPercent < 0 ||
                    !value.TryGetProperty("resets_at", out var reset) ||
                    reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out long resetSeconds) ||
                    resetSeconds <= 0 || resetSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                    continue;

                var window = new QuotaWindow(Math.Min(usedPercent, 100), DateTimeOffset.FromUnixTimeSeconds(resetSeconds));
                if (minutes == 300) fiveHour = window;
                if (minutes == 10080) weekly = window;
            }

            return fiveHour is not null || weekly is not null ? new(fiveHour, weekly, observed) : null;
        }
        catch (JsonException) { return null; }
    }

    static bool IsString(JsonElement value, string key, string expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.String && field.ValueEquals(expected);
}
