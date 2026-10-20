namespace AiPulse;

public record NetworkSample(string Id,string Name,bool Wireless,bool Connected,double? LinkMbps,double? DownloadMiBs,double? UploadMiBs);

public static class NetworkMetrics
{
    // Windows exposes the same traffic through filter-driver interfaces as well as
    // the adapter itself. Keep these aliases out of both totals and Wi-Fi choices.
    public static bool IsFilterAdapter(string name,string description)=>new[]{"-WFP ","-QoS Packet Scheduler-","-Native WiFi Filter Driver-"}
        .Any(s=>name.Contains(s,StringComparison.OrdinalIgnoreCase)||description.Contains(s,StringComparison.OrdinalIgnoreCase));
    public static List<NetworkSample> From(IEnumerable<Reading> readings)
    {
        var rows=readings.ToList();var values=rows.DistinctBy(r=>r.Id).ToDictionary(r=>r.Id,r=>r.Value);
        return rows.Where(r=>r.Id.StartsWith("nic.")&&r.Id.EndsWith(".up")).Select(r=>
        {
            var id=r.Id[..^3];return new NetworkSample(id,r.Device,values.GetValueOrDefault(id+".wireless")==1,r.Value==1,
                values.GetValueOrDefault(id+".speed"),values.GetValueOrDefault(id+".rx"),values.GetValueOrDefault(id+".tx"));
        }).OrderByDescending(n=>n.Connected).ThenBy(n=>n.Name).ToList();
    }
    public static double? Rate(long current,long previous,double elapsed)=>elapsed>0&&current>=previous?(current-previous)/elapsed/1048576.0:null;
    public const double MiBToMegabits=8.388608;
}
