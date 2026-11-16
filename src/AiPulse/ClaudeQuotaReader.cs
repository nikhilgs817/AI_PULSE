using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPulse;

// Claude Code's usage endpoint is not publicly documented and may change or refuse access.
// Both it and the sign-in renewal endpoint are rate limited, so every call is throttled here:
// one usage call per NormalInterval, exponential back-off after failures, and after HTTP 429/403
// no call at all until Retry-After (or a growing default) has passed. The wait is saved to disk so
// restarting AI Pulse cannot cause a burst of requests. Requests match what Claude Code sends.
internal static class ClaudeQuotaReader
{
    internal static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan RateLimitBase = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromHours(1);
    // Claude Code renews its own token shortly before expiry while it runs. Renew only once the
    // token is clearly past expiry, so AI Pulse and a running Claude Code do not renew together.
    internal static readonly TimeSpan RenewGrace = TimeSpan.FromMinutes(1);

    // Same endpoints, client id and body shape as Claude Code 2.1.x. The renewal endpoint moved from
    // console.anthropic.com to platform.claude.com; the old host answers 429.
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage?skip_spend=1";
    const string DefaultTokenUrl = "https://platform.claude.com/v1/oauth/token";
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    const string ClaudeCodeVersion = "2.1.325";
    const string UserAgent = "claude-cli/" + ClaudeCodeVersion + " (external, cli)";

    internal static string Status { get; private set; } = "Waiting for usage.";
    static HttpClient Client = NewClient(new HttpClientHandler { AllowAutoRedirect = false });

    static HttpClient NewClient(HttpMessageHandler handler) => new(handler) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Self-tests only: route requests to a fake server and forget in-memory state.</summary>
    internal static void UseForTests(HttpMessageHandler handler)
    {
        Client = NewClient(handler);
        throttle = null;
        Status = "Waiting for usage.";
    }

    // ---- Throttle state (persisted) -------------------------------------------------------

    internal sealed class Throttle
    {
        public long NextAttempt { get; set; }
        public int Failures { get; set; }
        public int RateLimitStrikes { get; set; }
        public bool RateLimited { get; set; }
        public string? DeadRefreshTokenHash { get; set; }
    }

    static Throttle? throttle;
    static Throttle State => throttle ??= LoadThrottle();
    internal static DateTimeOffset NextAttempt => DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, State.NextAttempt));
    /// <summary>True while Anthropic has asked AI Pulse to wait; a manual refresh cannot skip this.</summary>
    internal static bool WaitingForProvider(DateTimeOffset now) => State.RateLimited && now < NextAttempt;

    static string ThrottlePath()
    {
        var configured = Environment.GetEnvironmentVariable("AIPULSE_QUOTA_FILE");
        var directory = string.IsNullOrWhiteSpace(configured) ? Paths.DefaultRoot : Path.GetDirectoryName(Path.GetFullPath(configured))!;
        return Path.Combine(directory, "claude-usage-throttle.json");
    }

    static Throttle LoadThrottle()
    {
        try
        {
            var path = ThrottlePath();
            var loaded = File.Exists(path) ? JsonSerializer.Deserialize<Throttle>(File.ReadAllText(path)) : null;
            if (loaded is null) return new();
            // Never trust a wait longer than the maximum (e.g. after a clock change).
            var latest = DateTimeOffset.UtcNow + MaxRateLimitWait;
            if (loaded.NextAttempt > latest.ToUnixTimeSeconds()) loaded.NextAttempt = latest.ToUnixTimeSeconds();
            return loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    static void SaveThrottle()
    {
        try
        {
            var path = ThrottlePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(State));
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Paths.Log("claude-quota-throttle", e); }
    }

    static void Succeeded(DateTimeOffset observed)
    {
        State.Failures = 0; State.RateLimitStrikes = 0; State.RateLimited = false;
        State.NextAttempt = (observed + NormalInterval).ToUnixTimeSeconds();
        SaveThrottle();
    }

    static void Failed(DateTimeOffset now)
    {
        State.Failures = Math.Min(State.Failures + 1, 10);
        State.RateLimited = false;
        State.NextAttempt = (now + BackoffDelay(State.Failures, null)).ToUnixTimeSeconds();
        SaveThrottle();
    }

    static void RateLimitedFor(DateTimeOffset now, TimeSpan? retryAfter)
    {
        State.RateLimitStrikes = Math.Min(State.RateLimitStrikes + 1, 10);
        State.RateLimited = true;
        State.NextAttempt = (now + RateLimitDelay(State.RateLimitStrikes, retryAfter)).ToUnixTimeSeconds();
        SaveThrottle();
    }

    internal static TimeSpan BackoffDelay(int failures, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, Math.Pow(2, Math.Max(0, failures))));
        if (retryAfter is { } requested && requested > exponential)
            return requested > MaxRateLimitWait ? MaxRateLimitWait : requested;
        return exponential;
    }

    /// <summary>Wait after HTTP 429/403: Retry-After when given (0 is ignored, as the endpoint sends it
    /// misleadingly), otherwise 5, 10, 20, 40 minutes, never more than one hour.</summary>
    internal static TimeSpan RateLimitDelay(int strikes, TimeSpan? retryAfter)
    {
        var grown = TimeSpan.FromMinutes(Math.Min(MaxRateLimitWait.TotalMinutes,
            RateLimitBase.TotalMinutes * Math.Pow(2, Math.Max(0, strikes - 1))));
        var wait = retryAfter is { } requested && requested > TimeSpan.Zero ? requested : grown;
        if (wait < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
        return wait > MaxRateLimitWait ? MaxRateLimitWait : wait;
    }

    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header?.Delta is { } delta ? delta :
        header?.Date is { } date ? (date > now ? date - now : TimeSpan.Zero) : null;

    static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ---- Usage reading --------------------------------------------------------------------

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
        if (now < NextAttempt && (!manual || State.RateLimited)) return null;
        try
        {
            var credentialsPath = Path.Combine(ConfigDirectory(), ".credentials.json");
            var token = await AccessToken(credentialsPath, now, staleToken: null);
            if (token is null) return null;

            using var response = await SendUsage(token);
            HttpResponseMessage? retried = null;
            try
            {
                var final = response;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The token was revoked or expired early. Renew once and retry.
                    token = await AccessToken(credentialsPath, now, staleToken: token);
                    if (token is null) return null;
                    final = retried = await SendUsage(token);
                }
                if (final.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    RateLimitedFor(now, RetryAfter(final.Headers.RetryAfter, now));
                    Status = $"Anthropic is limiting usage requests (HTTP {(int)final.StatusCode}). " +
                             $"AI Pulse waits and retries at {NextAttempt.ToLocalTime():h:mm tt}; the last reading stays visible meanwhile.";
                    return null;
                }
                if (!final.IsSuccessStatusCode)
                {
                    Failed(now);
                    Status = final.StatusCode == HttpStatusCode.Unauthorized
                        ? "Claude rejected the sign-in. Run /login in Claude Code to reconnect usage."
                        : $"Usage endpoint returned HTTP {(int)final.StatusCode}. Next attempt {NextAttempt.ToLocalTime():h:mm tt}.";
                    return null;
                }
                using var document = JsonDocument.Parse(await final.Content.ReadAsStreamAsync());
                var observed = DateTimeOffset.UtcNow;
                var reading = Parse(document.RootElement, observed);
                Succeeded(observed);
                Status = reading is null ? "Provider returned no current usage windows." : "Live account usage.";
                return reading;
            }
            finally { retried?.Dispose(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or HttpRequestException or TaskCanceledException or FormatException)
        {
            Failed(now);
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
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return await Client.SendAsync(request);
    }

    // ---- OAuth token handling -------------------------------------------------------------

    /// <summary>Returns a usable access token, or null after setting Status and the throttle.</summary>
    /// <param name="staleToken">A token the provider just rejected; renew even if it looks unexpired.</param>
    static async Task<string?> AccessToken(string credentialsPath, DateTimeOffset now, string? staleToken)
    {
        var current = ReadCredentials(await File.ReadAllTextAsync(credentialsPath));
        if (current is null) { Failed(now); Status = "Claude Code sign-in not found. Sign in to Claude Code to show usage."; return null; }
        if (current.AccessToken != staleToken && current.ExpiresAt > now) return current.AccessToken;
        if (staleToken is null && current.ExpiresAt > now - RenewGrace)
        {
            Failed(now);
            Status = "Claude Code sign-in is renewing. Retrying automatically.";
            return null;
        }
        return await Renew(credentialsPath, now, staleToken);
    }

    internal sealed record Credentials(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);

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
        var scopes = oauth.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array
            ? sc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : Array.Empty<string>();
        return new(access.GetString()!, string.IsNullOrWhiteSpace(refresh) ? null : refresh, expires, scopes);
    }

    /// <summary>The renewal request body, identical in shape to Claude Code's.</summary>
    internal static string RenewalBody(Credentials credentials)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken ?? throw new InvalidOperationException("No refresh token."),
            ["client_id"] = ClientId,
        };
        if (credentials.Scopes.Count > 0) body["scope"] = string.Join(' ', credentials.Scopes);
        return JsonSerializer.Serialize(body);
    }

    static async Task<string?> Renew(string credentialsPath, DateTimeOffset now, string? staleToken)
    {
        using var guard = await AcquireLock();
        // Re-read under the lock: Claude Code or another AI Pulse process may already have renewed.
        var original = await File.ReadAllTextAsync(credentialsPath);
        var current = ReadCredentials(original);
        if (current is null) { Failed(now); Status = "Claude Code sign-in not found. Sign in to Claude Code to show usage."; return null; }
        if (current.AccessToken != staleToken && current.ExpiresAt > now) return current.AccessToken;
        if (current.RefreshToken is null || Hash(current.RefreshToken) == State.DeadRefreshTokenHash)
        {
            Failed(now);
            Status = "Claude Code sign-in expired. Open Claude Code (or run /login) once to reconnect usage.";
            return null;
        }

        var tokenUrl = Environment.GetEnvironmentVariable("AIPULSE_CLAUDE_TOKEN_URL");
        if (string.IsNullOrWhiteSpace(tokenUrl)) tokenUrl = DefaultTokenUrl;
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new StringContent(RenewalBody(current), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Client.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
        {
            // Throttled before the token was looked at: the refresh token is still valid. Do not retry soon.
            RateLimitedFor(now, RetryAfter(response.Headers.RetryAfter, now));
            Status = $"Anthropic is limiting sign-in renewal (HTTP {(int)response.StatusCode}). Opening Claude Code once renews the sign-in " +
                     $"immediately; otherwise AI Pulse retries at {NextAttempt.ToLocalTime():h:mm tt}.";
            return null;
        }
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // The refresh token was revoked or already rotated. Never retry it.
            State.DeadRefreshTokenHash = Hash(current.RefreshToken);
            Failed(now);
            Status = "Claude Code sign-in expired. Open Claude Code (or run /login) once to reconnect usage.";
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            Failed(now);
            Status = $"Claude sign-in renewal returned HTTP {(int)response.StatusCode}. Next attempt {NextAttempt.ToLocalTime():h:mm tt}.";
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
        var directory = Path.GetDirectoryName(ThrottlePath())!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "claude-oauth.lock");
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
