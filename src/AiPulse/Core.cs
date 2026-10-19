using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace AiPulse;

public record Reading(string Id, string Name, string Device, string Unit, double? Value, string Source);
public record ModelInfo(string Host, string Name, string State, double? MemoryGiB, double? GpuGiB, string Quantization, long Context, string Digest);
public record ProcessInfo(int Id, string Name, double Cpu, double MemoryMiB);
public record Usage(string Id, string Source, string Model, string Session, long Time, long Input, long Output, long Cached, long CacheWrite, long Reasoning, double? DurationMs, double? DecodeRate, string Status);
public record SessionInfo(string Id, string Source, string Model, string State, long LastSeen, long Input, long Output, long Cached);
public record MetricPoint(long Time, double Average, double Minimum, double Maximum);
public record AlertInfo(string Key, string Title, string Detail, long Time, bool Active);
public record Endpoint(string Name, string Url);
public record TokenBreakdown(string Source,string Model,long Input,long Output,long Cached,long CacheWrite,long Reasoning,long Uncached,long Records,long Incomplete)
{
    public bool OnPrem=>Source=="Ollama";
    public long Total=>Input+Output;
}

public sealed class AppSettings
{
    public bool Compact { get; set; } = true;
    public bool Pinned { get; set; } = true;
    public bool CompactVertical { get; set; }
    public bool SidebarCollapsed { get; set; }
    public bool RequestSensorsAtLaunch { get; set; }
    public int RetentionDays { get; set; } = 90;
    public bool StartAtLogin { get; set; }
    public bool ReadAgentUsage { get; set; } = true;
    public bool EnableGateway { get; set; } = true;
    public int GatewayPort { get; set; } = 11435;
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public List<Endpoint> RemoteEndpoints { get; set; } = [];
    public double? X { get; set; }
    public double? Y { get; set; }
    public double CompactWidth { get; set; } = 650;
    public double CompactHeight { get; set; } = 450;
    public double TemperatureWarning { get; set; } = 90;
    public string? BackupDirectory { get; set; }
    public string? LastBackup { get; set; }
    public string? LastBackupError { get; set; }
}

public static class Paths
{
    static readonly object settingsGate=new();
    public static string Root { get; private set; } = "";
    public static string Database => Path.Combine(Root,"data","pulse.db");
    public static string Settings => Path.Combine(Root,"settings.json");
    public static string Backups(AppSettings s) => string.IsNullOrWhiteSpace(s.BackupDirectory) ? Path.Combine(Root,"backups") : s.BackupDirectory;
    public static void Initialize(string[] args)
    {
        int ix=Array.IndexOf(args,"--data-dir");
        Root=ix>=0 && args.Length>ix+1 ? Path.GetFullPath(args[ix+1]) :
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI Pulse") :
            Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","share"),"ai-pulse");
        foreach(var sub in new[]{"data","backups","logs","exports"}) Directory.CreateDirectory(Path.Combine(Root,sub));
    }
    public static AppSettings Load()
    {
        if(!File.Exists(Settings)) return new();
        try {var settings=JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Settings)) ?? new();settings.RetentionDays=Math.Clamp(settings.RetentionDays,7,365);return settings;}
        catch { File.Copy(Settings,Settings+".invalid-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"),true); return new(); }
    }
    public static void Save(AppSettings settings)
    {
        lock(settingsGate){
        var temp=Settings+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,Settings,true);
        }
    }
    public static void Log(string component, Exception e)
    {
        // Exception bodies can contain request content. Persist only safe diagnostic classifications.
        try {var path=Path.Combine(Root,"logs","app.log"); if(File.Exists(path)&&new FileInfo(path).Length>2_000_000) File.Move(path,path+".previous",true);
        File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {component}: {e.GetType().Name} (0x{e.HResult:X})\n");} catch { }
    }
}

public static class J
{
    public static double? SensorValue(double? value,string unit)
    {
        if(value is not double number||!double.IsFinite(number))return null;
        if(unit=="%"&&(number<0||number>100))return null;
        if(unit=="°C"&&(number<=0||number>150))return null;
        if(unit is "GiB" or "MiB" or "B/s" or "RPM" or "MHz"&&number<0)return null;
        return number;
    }
    public static JsonElement Get(this JsonElement j,string key) => j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(key,out var v)?v:default;
    public static string Str(this JsonElement j,string key,string fallback="") {var v=j.Get(key);return v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;}
    public static long Num(this JsonElement j,string key) {var v=j.Get(key);return v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out var n)?n:0;}
    public static double? Dbl(this JsonElement j,string key) {var v=j.Get(key);return v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)?n:null;}
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static long Timestamp(JsonElement j) => DateTimeOffset.TryParse(j.Str("timestamp"),out var t)?t.ToUnixTimeSeconds():Now;
    public static string Count(long n) => n>=1_000_000?$"{n/1_000_000.0:0.00}M":n>=1000?$"{n/1000.0:0.0}k":n.ToString("N0");
    public static string Size(double? v) => v.HasValue?$"{v:0.0} GiB":"—";
}
