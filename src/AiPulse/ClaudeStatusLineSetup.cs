using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPulse;

// The status-line feed is Claude Code's documented way to expose subscription windows
// (rate_limits.five_hour / seven_day). It needs no network call and no credentials, so it is the
// primary Claude quota source. This helper wires it into Claude Code's user settings on request.
internal static class ClaudeStatusLineSetup
{
    internal const string Flag = "--claude-quota-statusline";
    internal enum State { Connected, OtherStatusLine, NotConfigured, Unreadable }

    internal static string SettingsPath(string? configDirectory = null) =>
        Path.Combine(configDirectory ?? ClaudeQuotaReader.ConfigDirectory(), "settings.json");

    // The user's own status-line command, kept beside the quota cache while AI Pulse wraps it.
    internal static string ChainPath() => Path.Combine(Path.GetDirectoryName(ClaudeQuotaStatusLine.CachePath())!, "claude-statusline-chain.json");

    internal static string Command(string executable, bool chain = false) =>
        "\"" + executable.Replace('\\', '/') + "\" " + Flag + (chain ? " " + ClaudeQuotaStatusLine.ChainFlag : "");

    internal static State Inspect(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return State.NotConfigured;
            var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            if (root?["statusLine"] is not JsonObject line) return State.NotConfigured;
            return (line["command"]?.GetValueKind() == JsonValueKind.String && line["command"]!.GetValue<string>().Contains(Flag, StringComparison.Ordinal))
                ? State.Connected : State.OtherStatusLine;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return State.Unreadable; }
    }

    /// <summary>
    /// Adds the AI Pulse status line. An existing status line is replaced only when
    /// <paramref name="wrapExisting"/> is set; its command is then kept and still runs and prints
    /// exactly as before, after AI Pulse records the limits.
    /// </summary>
    internal static State Connect(string settingsPath, string executable, bool wrapExisting = false, string? chainPath = null)
    {
        var current = Inspect(settingsPath);
        if (current is State.Connected or State.Unreadable) return current;
        if (current is State.OtherStatusLine && !wrapExisting) return current;
        JsonObject root = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject() : new JsonObject();
        bool chain = false;
        if (root["statusLine"] is JsonObject existing)
        {
            if (existing["command"]?.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(existing["command"]!.GetValue<string>()))
                return State.OtherStatusLine;
            var chainFile = chainPath ?? ChainPath();
            Directory.CreateDirectory(Path.GetDirectoryName(chainFile)!);
            File.WriteAllText(chainFile, new JsonObject { ["command"] = existing["command"]!.GetValue<string>() }.ToJsonString());
            existing["command"] = Command(executable, chain = true);
        }
        else root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command(executable) };
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        if (File.Exists(settingsPath)) File.Copy(settingsPath, settingsPath + ".aipulse-backup", overwrite: true);
        string temporary = settingsPath + ".aipulse-tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, settingsPath, overwrite: true);
        return chain || current is State.NotConfigured ? State.Connected : current;
    }

    /// <summary>Restores the user's own status line (or removes AI Pulse's) and forgets the chain.</summary>
    internal static void Disconnect(string settingsPath, string? chainPath = null)
    {
        if (Inspect(settingsPath) != State.Connected) return;
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
        var chainFile = chainPath ?? ChainPath();
        if (ReadChained(chainFile) is { } original && root["statusLine"] is JsonObject line) line["command"] = original;
        else root.Remove("statusLine");
        File.Copy(settingsPath, settingsPath + ".aipulse-backup", overwrite: true);
        string temporary = settingsPath + ".aipulse-tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, settingsPath, overwrite: true);
        try { File.Delete(chainFile); } catch (IOException) { }
    }

    internal static string? ReadChained(string? chainPath = null)
    {
        try
        {
            var path = chainPath ?? ChainPath();
            if (!File.Exists(path)) return null;
            var command = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["command"];
            return command?.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(command.GetValue<string>()) ? command.GetValue<string>() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }
}
