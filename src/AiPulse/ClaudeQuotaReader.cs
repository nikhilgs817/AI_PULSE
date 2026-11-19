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
    // Claude Code renews its own token shortly before expiry while it runs. Renew only once the
    // token is clearly past expiry, so AI Pulse and a running Claude Code do not renew together.
    internal static readonly TimeSpan RenewGrace = TimeSpan.FromMinutes(1);

    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string DefaultTokenUrl = "https://console.anthropic.com/v1/oauth/token";
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    // Status changes are logged (fixed messages, no tokens) so a blank Claude dial can be diagnosed from app.log.
    static string status = "Waiting for usage.";
    internal static string Status { get => status; private set { if (value != status) Paths.Note("claude-quota", value); status = value; } }
    internal static DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    static bool rateLimited;
    static int failures;
    static string? deadRefreshToken;
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
            if (token is null) return NoToken(now);

            using var response = await SendUsage(token);
            HttpResponseMessage? retried = null;
            try
            {
                var final = response;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The token was revoked or expired early. Renew once and retry.
                    token = await AccessToken(credentialsPath, now, staleToken: token);
                    if (token is null) return NoToken(now);
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
    // Renewal refused with HTTP 429: wait as long as the sign-in server asked before trying again.
    static TimeSpan? renewalRetryAfter;
    static ProviderQuota? NoToken(DateTimeOffset now)
    {
        var requested = renewalRetryAfter; renewalRetryAfter = null;
        Backoff(now, requested);
        if (requested is not null)
        {
            rateLimited = true;
            Status = $"Anthropic is rate-limiting sign-in renewal. Next attempt {NextAttempt.ToLocalTime():h:mm tt}. Using Claude Code in a terminal renews the sign-in sooner.";
        }
        return null;
    }

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
        if (staleToken is null && current.ExpiresAt > now - RenewGrace)
        {
            Status = "Claude Code sign-in is renewing. Retrying automatically.";
            return null;
        }
        return await Renew(credentialsPath, now, staleToken);
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

    static async Task<string?> Renew(string credentialsPath, DateTimeOffset now, string? staleToken)
    {
        using var guard = await AcquireLock();
        // Re-read under the lock: Claude Code or another AI Pulse process may already have renewed.
        var original = await File.ReadAllTextAsync(credentialsPath);
        var current = ReadCredentials(original);
        if (current is null) { Status = "Claude Code sign-in not found. Sign in to Claude Code to show usage."; return null; }
        if (current.AccessToken != staleToken && current.ExpiresAt > now) return current.AccessToken;
        if (current.RefreshToken is null || current.RefreshToken == deadRefreshToken)
        {
            Status = "Claude Code sign-in expired. Run /login in Claude Code to reconnect usage.";
            return null;
        }

        var tokenUrl = Environment.GetEnvironmentVariable("AIPULSE_CLAUDE_TOKEN_URL");
        if (string.IsNullOrWhiteSpace(tokenUrl)) tokenUrl = DefaultTokenUrl;
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = ClientId,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.UserAgent.ParseAdd("claude-code/2.1.307");
        using var response = await Client.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // The refresh token was revoked or already rotated. Never retry it.
            deadRefreshToken = current.RefreshToken;
            Status = "Claude Code sign-in expired. Run /login in Claude Code to reconnect usage.";
            return null;
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            renewalRetryAfter = RetryAfter(response.Headers.RetryAfter, now) ?? TimeSpan.FromMinutes(30);
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            Status = $"Claude sign-in renewal returned HTTP {(int)response.StatusCode}. Retrying automatically.";
            return null;
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var updated = ApplyRenewal(original, document.RootElement, DateTimeOffset.UtcNow);
        var renewed = ReadCredentials(updated)!;

        // Only replace the file if nobody changed it while the request was in flight.
        if (await File.ReadAllTextAsync(credentialsPath) == original) WriteAtomically(credentialsPath, updated);
        else Paths.Log("claude-quota", new IOException("Credentials changed during renewal; kept the newer file."));
        return renewed.AccessToken;
    }

    /// <summary>Applies an OAuth token response to Claude Code's credentials JSON, keeping every other field.</summary>
    internal static string ApplyRenewal(string credentialsJson, JsonElement tokenResponse, DateTimeOffset now)
    {
        var root = JsonNode.Parse(credentialsJson)?.AsObject() ?? throw new JsonException("Credentials are not an object.");
        var oauth = root["claudeAiOauth"]?.AsObject() ?? throw new JsonException("Credentials have no OAuth entry.");
        if (!tokenResponse.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(access.GetString()))
            throw new JsonException("Token response has no access token.");
        oauth["accessToken"] = access.GetString();
        if (tokenResponse.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(refresh.GetString()))
            oauth["refreshToken"] = refresh.GetString();
        long seconds = tokenResponse.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number &&
                       expires.TryGetInt64(out var s) && s > 0 ? s : 3600;
        oauth["expiresAt"] = now.AddSeconds(seconds).ToUnixTimeMilliseconds();
        if (tokenResponse.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(scope.GetString()))
            oauth["scopes"] = new JsonArray(scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".aipulse-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static async Task<IDisposable> AcquireLock()
    {
        Directory.CreateDirectory(Paths.DefaultRoot);
        var path = Path.Combine(Paths.DefaultRoot, "claude-oauth.lock");
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (attempt < 40) { await Task.Delay(250); }
        }
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
