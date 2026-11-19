using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPulse;

// Claude Code's usage endpoint is not publicly documented and may change or refuse access.
// It is also heavily rate limited, so every call is throttled here: one call per NormalInterval,
// exponential back-off after failures, and Retry-After is always honoured after HTTP 429.
internal static class ClaudeQuotaReader
{
    internal static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    // Read-only by design: AI Pulse never renews or rewrites Claude Code's sign-in. Refresh tokens
    // rotate, so a second renewer can invalidate Claude Code's own session. An expired token simply
    // waits until Claude Code renews it on its next use; the status-line feed covers the gap.

    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    internal static string Status { get; private set; } = "Waiting for usage.";
    internal static DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    static bool rateLimited;
    static int failures;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    internal static string ConfigDirectory()
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : home;
    }

    /// <summary>Returns a live reading, or null when the call was skipped (throttled) or failed.</summary>
    internal static async Task<ProviderQuota?> Read(DateTimeOffset now, bool manual = false)
    {
        // A manual refresh may skip the normal interval, but never a provider-requested wait.
        if (now < NextAttempt && (!manual || rateLimited)) return null;
        try
        {
            var credentialsPath = Path.Combine(ConfigDirectory(), ".credentials.json");
            var token = await AccessToken(credentialsPath, now, staleToken: null);
            if (token is null) { Backoff(now, null); return null; }

            using var response = await SendUsage(token);
            HttpResponseMessage? retried = null;
            try
            {
                var final = response;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The token was revoked or expired early. Re-read once in case Claude Code just renewed it.
                    token = await AccessToken(credentialsPath, now, staleToken: token);
                    if (token is null) { Backoff(now, null); return null; }
                    final = retried = await SendUsage(token);
                }
                if (final.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Backoff(now, RetryAfter(final.Headers.RetryAfter, now));
                    rateLimited = true;
                    Status = $"Anthropic rate-limited usage requests. Next attempt {NextAttempt.ToLocalTime():h:mm tt}.";
                    return null;
                }
                if (!final.IsSuccessStatusCode)
                {
                    Backoff(now, null);
                    Status = final.StatusCode == HttpStatusCode.Unauthorized
                        ? "Claude rejected the sign-in. Run /login in Claude Code to reconnect usage."
                        : $"Usage endpoint returned HTTP {(int)final.StatusCode}. Next attempt {NextAttempt.ToLocalTime():h:mm tt}.";
                    return null;
                }
                using var document = JsonDocument.Parse(await final.Content.ReadAsStreamAsync());
                var observed = DateTimeOffset.UtcNow;
                var reading = Parse(document.RootElement, observed);
                failures = 0; rateLimited = false;
                NextAttempt = observed + NormalInterval;
                Status = reading is null ? "Provider returned no current usage windows." : "Live account usage.";
                return reading;
            }
            finally { retried?.Dispose(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or HttpRequestException or TaskCanceledException or FormatException)
        {
            Backoff(now, null);
            Status = e is TaskCanceledException ? "Usage request timed out. Retrying automatically." :
                e is FileNotFoundException or DirectoryNotFoundException ? "Claude Code sign-in not found. Sign in to Claude Code to show usage." :
                "Usage connection unavailable. Retrying automatically.";
            if (e is not FileNotFoundException and not DirectoryNotFoundException) Paths.Log("claude-quota", e);
            return null;
        }
    }

    static async Task<HttpResponseMessage> SendUsage(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-code/2.1.307");
        return await Client.SendAsync(request);
    }

    /// <summary>Schedules the next attempt after a failure and returns the chosen wait.</summary>
    static TimeSpan Backoff(DateTimeOffset now, TimeSpan? requested)
    {
        failures = Math.Min(failures + 1, 10);
        var wait = BackoffDelay(failures, requested);
        NextAttempt = now + wait;
        rateLimited = false;
        return wait;
    }

    internal static TimeSpan BackoffDelay(int failures, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, Math.Pow(2, Math.Max(0, failures))));
        if (retryAfter is { } requested && requested > exponential)
            return requested > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : requested;
        return exponential;
    }

    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header?.Delta is { } delta ? delta :
        header?.Date is { } date ? (date > now ? date - now : TimeSpan.Zero) : null;

    // ---- OAuth token handling -------------------------------------------------------------

    /// <param name="staleToken">A token the provider just rejected; renew even if it looks unexpired.</param>
    static async Task<string?> AccessToken(string credentialsPath, DateTimeOffset now, string? staleToken)
    {
        var current = ReadCredentials(await File.ReadAllTextAsync(credentialsPath));
        if (current is null) { Status = "Claude Code sign-in not found. Sign in to Claude Code to show usage."; return null; }
        if (current.AccessToken != staleToken && current.ExpiresAt > now) return current.AccessToken;
        Status = "Waiting for Claude Code to renew its sign-in (it does on next use). Status-line readings still update.";
        return null;
    }

    internal sealed record Credentials(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    internal static Credentials? ReadCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
            !oauth.TryGetProperty("accessToken", out var access) || access.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(access.GetString()))
            return null;
        var refresh = oauth.TryGetProperty("refreshToken", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        // A missing expiry is treated as valid; the usage call's 401 handling covers a dead token.
        var expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()))
            : DateTimeOffset.MaxValue;
        return new(access.GetString()!, string.IsNullOrWhiteSpace(refresh) ? null : refresh, expires);
    }

    // ---- Response parsing -----------------------------------------------------------------

    internal static ProviderQuota? Parse(JsonElement root, DateTimeOffset now)
    {
        QuotaWindow? Window(string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number ||
                !used.TryGetDouble(out double percent) || !double.IsFinite(percent) || percent < 0)
                return null;
            DateTimeOffset? resetsAt = null;
            if (value.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String)
            {
                if (!DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    return null;
                if (parsed <= now) return new(0, null, now); // The window has already reset.
                resetsAt = parsed;
            }
            // A window that has not started yet has no reset time; it is a valid 0%-used reading.
            return new(Math.Min(percent, 100), resetsAt, now);
        }
        var fiveHour = Window("five_hour");
        var weekly = Window("seven_day");
        return fiveHour is null && weekly is null ? null : new(fiveHour, weekly, now);
    }
}
