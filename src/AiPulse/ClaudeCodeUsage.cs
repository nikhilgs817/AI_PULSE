using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPulse;

/// <summary>
/// Claude subscription usage without AI Pulse ever calling Anthropic. AI Pulse only reads what
/// Claude Code itself already has, so there is nothing to rate-limit and no sign-in to renew:
/// <list type="number">
/// <item>Claude Code's status-line input: <c>rate_limits</c> taken from the headers of Claude Code's own
/// replies, recorded by <c>AiPulse --claude-quota-statusline</c> (see <see cref="ClaudeStatusLineSetup"/>).</item>
/// <item>Claude Code's saved usage reading: <c>cachedUsageUtilization</c> in <c>.claude.json</c>,
/// written whenever Claude Code fetches usage (for example <c>/usage</c>).</item>
/// </list>
/// </summary>
internal static class ClaudeCodeUsage
{
    /// <summary>Claude Code's settings directory (<c>~/.claude</c> unless CLAUDE_CONFIG_DIR is set).</summary>
    internal static string ConfigDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    /// <summary>Claude Code's global state file: <c>~/.claude.json</c>, or <c>.claude.json</c> inside CLAUDE_CONFIG_DIR.</summary>
    internal static string GlobalStatePath()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : Path.Combine(configured, ".claude.json");
    }

    static (string Path, DateTime WrittenUtc, long Length, ProviderQuota? Reading)? savedCache;

    /// <summary>Claude Code's last saved usage reading, re-parsed only when the file changes.</summary>
    internal static ProviderQuota? ReadSaved()
    {
        var path = GlobalStatePath();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (savedCache is { } cached && cached.Path == path && cached.WrittenUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                return cached.Reading;
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                json = reader.ReadToEnd();
            var reading = ParseSaved(json);
            savedCache = (path, info.LastWriteTimeUtc, info.Length, reading);
            return reading;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Claude Code may be rewriting the file; the next read gets the finished version.
            return savedCache is { } cached && cached.Path == path ? cached.Reading : null;
        }
    }

    /// <summary>Reads <c>cachedUsageUtilization</c> from the contents of Claude Code's <c>.claude.json</c>.</summary>
    internal static ProviderQuota? ParseSaved(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("cachedUsageUtilization", out var saved) || saved.ValueKind != JsonValueKind.Object ||
            !saved.TryGetProperty("fetchedAtMs", out var fetched) || fetched.ValueKind != JsonValueKind.Number ||
            !fetched.TryGetInt64(out var fetchedAtMs) || fetchedAtMs <= 0 ||
            fetchedAtMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            !saved.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Object)
            return null;
        // Claude Code discards a reading saved for another account; do the same.
        string? savedAccount = saved.TryGetProperty("accountUuid", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        string? currentAccount = root.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object &&
                                 account.TryGetProperty("accountUuid", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (savedAccount is not null && currentAccount is not null && savedAccount != currentAccount) return null;
        return ParseUsage(utilization, DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs));
    }

    /// <summary>Parses a Claude usage object (<c>five_hour</c>/<c>seven_day</c> with <c>utilization</c> and ISO <c>resets_at</c>).</summary>
    internal static ProviderQuota? ParseUsage(JsonElement root, DateTimeOffset observedAt)
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
                if (parsed <= observedAt) return new(0, null, observedAt); // Already reset when it was read.
                resetsAt = parsed;
            }
            // A window that has not started yet has no reset time; it is a valid 0%-used reading.
            return new(Math.Min(percent, 100), resetsAt, observedAt);
        }
        var fiveHour = Window("five_hour");
        var weekly = Window("seven_day");
        return fiveHour is null && weekly is null ? null : new(fiveHour, weekly, observedAt);
    }

    /// <summary>One line telling the user where the Claude numbers come from and what to do if there are none.</summary>
    internal static string Describe(ProviderQuota merged, ClaudeStatusLineSetup.Connection connection, DateTimeOffset now)
    {
        var observed = merged.ObservedAt;
        if (observed is { } seen)
        {
            var age = now - seen;
            var ago = age < TimeSpan.FromMinutes(1) ? "just now" :
                age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago" :
                age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago" : seen.ToLocalTime().ToString("MMM d, h:mm tt");
            return $"From Claude Code, updated {ago}." +
                   (age > TimeSpan.FromMinutes(15) ? " It refreshes with your next Claude Code message." : "");
        }
        return connection switch
        {
            ClaudeStatusLineSetup.Connection.NoClaudeCode => "Claude Code was not found on this computer.",
            ClaudeStatusLineSetup.Connection.Connected => "Waiting for your next Claude Code message.",
            _ => "No reading yet. Connect Claude Code below, or run /usage in Claude Code.",
        };
    }
}

/// <summary>
/// Adds AI Pulse to Claude Code's status line in <c>settings.json</c>, and removes it again. A status
/// line the user already has is kept: AI Pulse records the usage numbers, then runs that command with
/// the same input and prints its output, so the user's status line looks exactly as before.
/// </summary>
internal static class ClaudeStatusLineSetup
{
    internal const string Flag = "--claude-quota-statusline";

    internal enum Connection { NoClaudeCode, NotConnected, Connected, ConnectedToOtherCopy }

    internal sealed record State(Connection Connection, string? ExistingCommand);

    internal static string SettingsPath() => Path.Combine(ClaudeCodeUsage.ConfigDirectory(), "settings.json");

    static string ChainPath() =>
        Path.Combine(Path.GetDirectoryName(ClaudeQuotaStatusLine.CachePath())!, "claude-statusline-chain.json");

    /// <summary>The program Claude Code should run: the AppImage itself when running from one.</summary>
    internal static string? ExecutablePath()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage) ? appImage : Environment.ProcessPath;
    }

    // Claude Code runs status-line commands through a shell (Git Bash on Windows): quote the path, use forward slashes.
    internal static string CommandFor(string executable) => "\"" + executable.Replace('\\', '/') + "\" " + Flag;

    internal static bool IsOurs(string? command) => command?.Contains(Flag, StringComparison.Ordinal) == true;

    internal static State Inspect(string? executable)
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
                return new(Directory.Exists(ClaudeCodeUsage.ConfigDirectory()) ? Connection.NotConnected : Connection.NoClaudeCode, null);
            var command = ParseSettings(File.ReadAllText(path))["statusLine"]?["command"]?.GetValue<string>();
            if (!IsOurs(command)) return new(Connection.NotConnected, string.IsNullOrWhiteSpace(command) ? null : command);
            var wrapped = WrappedCommand();
            return new(executable is not null && command != CommandFor(executable) ? Connection.ConnectedToOtherCopy : Connection.Connected, wrapped);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new(Connection.NotConnected, null);
        }
    }

    /// <summary>Points Claude Code's status line at AI Pulse, keeping any existing status line.</summary>
    internal static void Connect(string executable)
    {
        if (!Directory.Exists(ClaudeCodeUsage.ConfigDirectory()))
            throw new InvalidOperationException("Claude Code was not found on this computer. Start Claude Code once, then try again.");
        var path = SettingsPath();
        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        var settings = ParseSettings(original);
        var existing = settings["statusLine"] as JsonObject;
        var existingCommand = existing?["command"]?.GetValue<string>();

        JsonObject updated;
        if (IsOurs(existingCommand))
        {
            updated = (JsonObject)existing!.DeepClone(); // Reconnect, e.g. after AI Pulse moved; keep what it wraps.
        }
        else
        {
            if (original is not null) File.WriteAllText(path + ".before-aipulse", original);
            if (existing is not null && !string.IsNullOrWhiteSpace(existingCommand))
            {
                WriteAtomically(ChainPath(), new JsonObject { ["statusLine"] = existing.DeepClone() }.ToJsonString(Indented));
                updated = (JsonObject)existing.DeepClone();
            }
            else
            {
                DeleteChain();
                updated = new JsonObject();
            }
        }
        updated["type"] = "command";
        updated["command"] = CommandFor(executable);
        settings["statusLine"] = updated;
        WriteAtomically(path, settings.ToJsonString(Indented));
    }

    /// <summary>Removes AI Pulse from Claude Code's status line and restores the user's previous one.</summary>
    internal static void Disconnect()
    {
        var path = SettingsPath();
        if (!File.Exists(path)) { DeleteChain(); return; }
        var settings = ParseSettings(File.ReadAllText(path));
        if (!IsOurs(settings["statusLine"]?["command"]?.GetValue<string>())) return; // The user changed it; leave it alone.
        var previous = ReadChain();
        if (previous is not null) settings["statusLine"] = previous;
        else settings.Remove("statusLine");
        WriteAtomically(path, settings.ToJsonString(Indented));
        DeleteChain();
    }

    /// <summary>The user's own status-line command that AI Pulse runs after recording usage, if any.</summary>
    internal static string? WrappedCommand()
    {
        var command = ReadChain()?["command"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(command) || IsOurs(command) ? null : command;
    }

    /// <summary>Runs the wrapped status line with Claude Code's input; null when there is none or it failed.</summary>
    internal static string? RunWrapped(string input)
    {
        var command = WrappedCommand();
        if (command is null) return null;
        string? cwd = null;
        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.Object &&
                workspace.TryGetProperty("current_dir", out var dir) && dir.ValueKind == JsonValueKind.String) cwd = dir.GetString();
            else if (root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String) cwd = c.GetString();
        }
        catch (JsonException) { }
        return RunCommand(command, input, cwd, TimeSpan.FromSeconds(8));
    }

    internal static string? RunCommand(string command, string input, string? workingDirectory, TimeSpan timeout)
    {
        var start = Shell(command);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = new UTF8Encoding(false);
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)) start.WorkingDirectory = workingDirectory;
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(input); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return output.GetAwaiter().GetResult().TrimEnd('\r', '\n');
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    static ProcessStartInfo Shell(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var bash = FindGitBash();
            if (bash is null) return new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" };
            var start = new ProcessStartInfo(bash);
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
            return start;
        }
        var shell = new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
        shell.ArgumentList.Add("-c"); shell.ArgumentList.Add(command);
        return shell;
    }

    static string? FindGitBash()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static JsonObject ParseSettings(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidOperationException("Claude Code settings.json is not a JSON object; it was not changed.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Claude Code settings.json is not valid JSON; it was not changed.");
        }
    }

    static JsonObject? ReadChain()
    {
        try
        {
            var path = ChainPath();
            return File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["statusLine"]?.DeepClone() as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static void DeleteChain()
    {
        try { File.Delete(ChainPath()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".aipulse-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content + "\n", new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
