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

    internal static string Command(string executable) => "\"" + executable.Replace('\\', '/') + "\" " + Flag;

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

    // Adds the status line only when none exists; never replaces a user's own status line.
    internal static State Connect(string settingsPath, string executable)
    {
        var current = Inspect(settingsPath);
        if (current is not State.NotConfigured) return current;
        JsonObject root = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject() : new JsonObject();
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command(executable) };
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        if (File.Exists(settingsPath)) File.Copy(settingsPath, settingsPath + ".aipulse-backup", overwrite: true);
        string temporary = settingsPath + ".aipulse-tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, settingsPath, overwrite: true);
        return State.Connected;
    }
}
