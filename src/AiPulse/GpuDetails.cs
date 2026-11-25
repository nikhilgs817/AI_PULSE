using System.Diagnostics;
using System.Globalization;

namespace AiPulse;

/// <summary>One process holding NVIDIA GPU memory. Memory is null where the driver does not report it (common on Windows WDDM).</summary>
public sealed record GpuProcess(int Pid, string Name, double? MemoryMiB);

/// <summary>Why NVIDIA clocks are held down right now. Idle and application-clock reasons are not counted as throttling.</summary>
public sealed record GpuThrottle(bool Active, string Reasons, ulong Mask);

/// <summary>Reads per-process GPU memory and clock throttle reasons from nvidia-smi, when it is installed.</summary>
public static class GpuDetails
{
    static readonly (ulong Bit, string Name, bool Counts)[] Bits =
    [
        (0x1, "idle", false), (0x2, "application clocks", false), (0x4, "power cap", true), (0x8, "hardware slowdown", true),
        (0x10, "sync boost", false), (0x20, "thermal (driver)", true), (0x40, "thermal (hardware)", true), (0x80, "power brake", true),
        (0x100, "display clocks", false),
    ];

    public static List<GpuProcess> ParseProcesses(string csv)
    {
        var list = new List<GpuProcess>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out int pid)) continue;
            double? memory = double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : null;
            string name = string.Join(",", parts[1..^1]);
            list.Add(new(pid, Path.GetFileName(name.Replace('\\', '/')), memory));
        }
        return list.OrderByDescending(p => p.MemoryMiB ?? -1).ToList();
    }

    public static GpuThrottle? ParseThrottle(string csv)
    {
        var first = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first == null) return null;
        string hex = first.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? first[2..] : first;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong mask)) return null;
        var reasons = Bits.Where(b => (mask & b.Bit) != 0 && b.Counts).Select(b => b.Name).ToList();
        return new(reasons.Count > 0, reasons.Count > 0 ? string.Join(", ", reasons) : "none", mask);
    }

    static string? tool;
    static string? Tool()
    {
        if (tool != null) return tool == "" ? null : tool;
        string exe = OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, exe)).ToList();
        if (OperatingSystem.IsWindows()) candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe));
        tool = candidates.FirstOrDefault(File.Exists) ?? "";
        return tool == "" ? null : tool;
    }

    static string? Run(string arguments)
    {
        if (Tool() is not { } path) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return null; }
    }

    /// <summary>Returns null values when nvidia-smi is missing or does not answer within 3 seconds.</summary>
    public static (List<GpuProcess>? Processes, GpuThrottle? Throttle) Read()
    {
        if (Tool() == null) return (null, null);
        var processes = Run("--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits") is { } p ? ParseProcesses(p) : null;
        var throttle = Run("--query-gpu=clocks_throttle_reasons.active --format=csv,noheader") is { } t ? ParseThrottle(t)
            : Run("--query-gpu=clocks_event_reasons.active --format=csv,noheader") is { } e ? ParseThrottle(e) : null;
        return (processes, throttle);
    }
}
