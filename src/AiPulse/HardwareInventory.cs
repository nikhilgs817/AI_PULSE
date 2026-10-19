using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AiPulse;

public record HardwareItem(string Category,string Name,string Details);

public static class HardwareInventory
{
    public static bool IsStorageVolume(DriveInfo disk)=>disk.IsReady&&disk.DriveType==DriveType.Fixed&&disk.TotalSize>0&&
        (OperatingSystem.IsWindows()||!new[]{"/proc","/sys","/dev"}.Any(p=>disk.Name==p||disk.Name.StartsWith(p+"/",StringComparison.Ordinal)));
    public static List<HardwareItem> Read(string cpu,string gpu,double? memoryGiB)
    {
        var rows=new List<HardwareItem>{
            new("CPU",cpu,$"{Environment.ProcessorCount} available logical processors · {RuntimeInformation.OSArchitecture}"),
            new("GPU",gpu,"Graphics / compute adapter"),
            new("Memory","System RAM",memoryGiB.HasValue?$"{memoryGiB:0.0} GiB usable physical capacity":"Capacity unavailable"),
            new("Platform",RuntimeInformation.OSDescription,RuntimeInformation.OSArchitecture+" operating system")};
        foreach(var disk in DriveInfo.GetDrives())
        {
            try{if(IsStorageVolume(disk))rows.Add(new("Storage volume",disk.Name,$"{disk.TotalSize/1073741824.0:0.0} GiB capacity · {disk.DriveFormat}"));}catch{}
        }
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback))
            rows.Add(new("Network adapter",nic.Name,nic.Description+" · "+nic.NetworkInterfaceType));
        return rows;
    }
}
