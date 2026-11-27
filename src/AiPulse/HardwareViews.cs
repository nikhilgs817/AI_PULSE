using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using System.Diagnostics;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Interactivity;

namespace AiPulse;

// Hardware and Sensors pages: devices, disks, network adapters, raw sensor readings and processes.
public sealed partial class MainWindow
{
    string Format(string id)
    {
        if(service.IsRemote&&!service.Fresh)return "—";
        double? v=service.Value(id);if(id=="gpu.load"&&!v.HasValue)v=service.Value("gpu.engine");
        if(!v.HasValue)return "—";var unit=service.Readings.FirstOrDefault(r=>r.Id==id)?.Unit??"";
        return unit=="°C"||id.Contains("temperature")?$"{v:0}°C":unit=="%"||id.Contains("load")?$"{v:0}%":$"{v:0.0} {unit}".TrimEnd();
    }
    string? UsageLevel(string metric, string? capacity = null)
    {
        if (!service.Fresh) return null;
        var value = service.Value(metric);
        if (metric == "gpu.load") value ??= service.Value("gpu.engine");
        if (capacity is null) return Level(value);
        return service.Value(capacity) is > 0 and double total ? Level(100 * value / total, 80, 95) : null;
    }

    Reading? GpuVrSocSensor => service.Readings.FirstOrDefault(r =>
        r.Unit == "°C" && r.Name.Equals("GPU VR SoC", StringComparison.OrdinalIgnoreCase));
    string SensorDescription()
    {
        if(service.IsRemote)return service.Health+". Deep sensor permissions must be configured on the selected host.";
        if(service.Value("cpu.temperature")!=null)return "CPU temperature is live. GPU core temperature is shown only if the hardware exposes it; GPU VR SoC is a separate sensor.";
        if(!OperatingSystem.IsWindows())return "CPU temperature is not exposed by readable hwmon sensors. Check device support and sensor permissions.";
        if(service.SensorBridge.Connected)return "Deep sensor helper is connected, but this hardware/driver does not currently expose a CPU temperature. Other sensors remain live.";
        return service.SensorBridge.Status+". "+(SensorBridge.DriverInstalled?"PawnIO is installed. Connect deep sensors to read CPU temperature; Windows asks for administrator permission.":"The optional PawnIO driver is not installed. Install the signed driver, then connect deep sensors.");
    }
    Control SensorCard()
    {
        var content=new StackPanel{Spacing=10};content.Children.Add(Text("Sensor access",13,"#bba3ff",true));content.Children.Add(Text(SensorDescription(),12,"#9eb1ca"));
        if(!service.IsRemote&&OperatingSystem.IsWindows()&&!service.SensorBridge.Connected)
        {
            var enable=Btn(service.SensorBridge.Connecting?"Connecting…":"Connect deep sensors");enable.IsEnabled=!service.SensorBridge.Connecting;
            enable.Click+=(_,_)=>{service.SensorBridge.Enable();RenderBody();};content.Children.Add(enable);
        }
        return Card(content);
    }
    Control HardwarePage()
    {
        var stack=new StackPanel{Spacing=16};stack.Children.Add(Heading("Hardware","Live capacity and consumption · temperatures, fans, power and clocks are in Sensors"));
        var cards=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=12,LineSpacing=12};
        void Capacity(string title,string metric,string detail,string? device=null,string? total=null)
        {
            if(!Matches(title,detail,device??""))return;
            var card=Metric(title,Format(metric),detail,UsageLevel(metric,total));card.Width=280;card.MinHeight=140;cards.Children.Add(card);
        }
        Capacity("CPU UTILIZATION","cpu.load",$"{service.LogicalProcessors} logical processors · % of total host CPU",service.CpuName);
        Capacity("GPU UTILIZATION","gpu.load",service.GpuName??"Graphics / compute adapter");
        Capacity("SYSTEM MEMORY","ram.used","Used of "+Format("ram.total")+" total · "+Format("ram.available")+" available",total:"ram.total");
        Capacity("GPU LOCAL MEMORY","gpu.local","Allocated of "+Format("gpu.local.total")+" reported capacity",service.GpuName,"gpu.local.total");
        Capacity("GPU SHARED MEMORY","gpu.shared","Allocated of "+Format("gpu.shared.total")+" reported limit · overlaps system RAM",service.GpuName,"gpu.shared.total");
        stack.Children.Add(cards);
        var disks=service.Readings.Where(r=>r.Id.StartsWith("disk.")&&r.Id.EndsWith(".size")&&Matches("Storage volume disk",r.Device)).ToList();
        if(disks.Count>0)
        {
            stack.Children.Add(Text("Storage · capacity and current usage",16,null,true));
            stack.Children.Add(Table(["Volume","Total capacity","Used","Available","Used %"],disks.Select(r=>
            {
                var prefix=r.Id[..^5];var used=service.Value(prefix+".used");
                return new[]{r.Device,Format(r.Id),Format(prefix+".used"),Format(prefix+".free"),r.Value>0&&used.HasValue?$"{100*used/r.Value:0.0}%":"—"};
            }),"1.4*,*,*,*,*"));
        }
        var nics=service.Readings.Where(r=>r.Id.StartsWith("nic.")&&r.Id.EndsWith(".up")&&(r.Value==1||!string.IsNullOrWhiteSpace(Query))&&Matches("Network adapter",r.Device,r.Value==1?"Connected":"Disconnected")).OrderByDescending(r=>r.Value).ThenBy(r=>r.Device).ToList();
        if(nics.Count>0)
        {
            stack.Children.Add(Text("Network · link capacity and live traffic",16,null,true));
            stack.Children.Add(Text("Connected adapters shown by default. Search to include disconnected adapters; all identities are in Device specifications.",11,"#8ba0bd"));
            stack.Children.Add(Table(["Adapter","Status","Link capacity","Download","Upload"],nics.Select(r=>{var prefix=r.Id[..^3];return new[]{r.Device,r.Value==1?"Connected":"Disconnected",Format(prefix+".speed"),Format(prefix+".rx"),Format(prefix+".tx")};}),"2*,1.1*,*,*,*"));
        }
        var inventory=(service.Inventory).Where(r=>Matches(r.Category,r.Name,r.Details)).ToList();
        var details=new Expander{Header="Device specifications · "+inventory.Count+" matches",HorizontalAlignment=HorizontalAlignment.Stretch,IsExpanded=hardwareDetailsExpanded,Content=Table(["Category","Device","Specifications"],inventory.Select(r=>new[]{r.Category,r.Name,r.Details}),"*,2*,3*")};
        details.PropertyChanged+=(_,e)=>{if(e.Property==Expander.IsExpandedProperty)hardwareDetailsExpanded=details.IsExpanded;};stack.Children.Add(details);
        if(inventory.Count==0&&cards.Children.Count==0&&disks.Count==0&&nics.Count==0)stack.Children.Add(Text(service.Inventory.Count>0?"No hardware matches your search.":"Discovering hardware…",13,"#8da1bd"));
        return stack;
    }
    bool hardwareDetailsExpanded;
    Control SensorsPage()
    {
        var stack=new StackPanel{Spacing=16};stack.Children.Add(Heading("Sensors","Live temperatures, utilization, power, clocks, fans and resource activity · "+service.Health));stack.Children.Add(SensorCard());
        var readings=service.Readings.Where(r=>Matches(r.Device,r.Name,r.Source,r.Unit)).OrderBy(r=>r.Device).ThenBy(r=>r.Name).ToList();
        stack.Children.Add(Text("Sensor inventory · "+readings.Count+" matches",16,null,true));
        stack.Children.Add(Table(["Device","Sensor","Value","Unit","Source"],readings.Select(r=>new[]{r.Device,r.Name,r.Value?.ToString("0.##")??"Unavailable",r.Unit,r.Source}),"1.4*,2*,*,70,1.5*"));
        if(readings.Count==0)stack.Children.Add(Text("No sensors match your search.",13,"#8da1bd"));
        if(!service.IsRemote&&(service.GpuProcesses.Count>0||service.GpuThrottle!=null))
        {
            var gpu=new StackPanel{Spacing=10};gpu.Children.Add(Text("NVIDIA GPU processes and clocks",15,null,true));
            if(service.GpuThrottle is {} throttle)gpu.Children.Add(Text(throttle.Active?"Clocks held down: "+throttle.Reasons:"Clocks are not throttled",13,throttle.Active?"#e8b65f":"#59dfae",true));
            if(service.GpuProcesses.Count>0)gpu.Children.Add(Table(["Process","PID","GPU memory"],service.GpuProcesses.Select(p=>new[]{p.Name,p.Pid.ToString(),p.MemoryMiB is double m?$"{m:N0} MiB":"Not reported by the driver"}),"2*,*,*"));
            else gpu.Children.Add(Text("No process is using the GPU for compute right now.",12,"#8da1bd"));
            stack.Children.Add(Card(gpu));
        }
        stack.Children.Add(Text("Live process activity",16,null,true));stack.Children.Add(Table(["Process","PID","CPU (host %)","Working set MiB"],(service.Processes).Where(p=>Matches(p.Name,p.Id.ToString())).Select(p=>new[]{p.Name,p.Id.ToString(),p.Cpu?.ToString("0.0")??"—",p.MemoryMiB.ToString("N0")}),"2*,*,*,*"));return stack;
    }
}
