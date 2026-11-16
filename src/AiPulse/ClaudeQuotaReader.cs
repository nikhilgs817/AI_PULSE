using System.Net.Http.Headers;
using System.Text.Json;

namespace AiPulse;

// Claude Code's usage endpoint is not publicly documented and may change or refuse access.
internal static class ClaudeQuotaReader
{
    internal static string Status { get; private set; } = "Waiting for usage.";
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    internal static async Task<ProviderQuota?> Read(DateTimeOffset now)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".credentials.json")));
            var oauth = credentials.RootElement.GetProperty("claudeAiOauth");
            var token = oauth.GetProperty("accessToken").GetString();
            if (string.IsNullOrWhiteSpace(token) || !oauth.TryGetProperty("expiresAt", out var expiry) ||
                !expiry.TryGetInt64(out var expiresAt) || expiresAt <= now.ToUnixTimeMilliseconds()) { Status = "Claude Code sign-in expired. Sign in through Claude Code to reconnect usage."; return null; }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-code/2.1.307");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) { Status = $"Usage endpoint returned HTTP {(int)response.StatusCode}. Retrying automatically. Claude Code status-line readings are also supported."; return null; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var reading = Parse(document.RootElement, DateTimeOffset.UtcNow);
            Status = reading is null ? "Provider returned no current usage windows." : "Live account usage.";
            return reading;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or HttpRequestException or TaskCanceledException or FormatException)
        {
            Status = e is TaskCanceledException ? "Usage request timed out. Retrying automatically." : "Usage connection unavailable. Check Claude Code sign-in. Retrying automatically.";
            return null;
        }
    }

    internal static ProviderQuota? Parse(JsonElement root, DateTimeOffset now)
    {
        QuotaWindow? Window(string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("utilization", out var used) || !used.TryGetDouble(out double percent) ||
                !double.IsFinite(percent) || percent < 0 || !value.TryGetProperty("resets_at", out var reset) ||
                reset.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(reset.GetString(), out var resetsAt) || resetsAt <= now)
                return null;
            return new(Math.Min(percent, 100), resetsAt, now);
        }
        var fiveHour = Window("five_hour");
        var weekly = Window("seven_day");
        return fiveHour is null && weekly is null ? null : new(fiveHour, weekly, now);
    }
}

