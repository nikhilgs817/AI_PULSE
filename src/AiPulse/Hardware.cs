using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Globalization;

namespace AiPulse;

public sealed class Hardware : IDisposable
{
    IWindowsSensors? computer;
    public string Status {get;private set;}="Starting collectors";
    public string CpuName {get;private set;}="CPU";
    public string GpuName {get;private set;}="GPU";
    public List<ProcessInfo> Processes {get;private set;}=[];
    public List<AgentResource> AgentApps {get;private set;}=[];
    public DateTime ProcessSampleTime {get;private set;}
    public int UnreadableProcesses {get;private set;}
    public List<HardwareItem> Inventory {get;private set;}=[];
    DateTime nextInventory=DateTime.MinValue;
    ulong lastIdle,lastKernel,lastUser;
    long linuxTotal,linuxIdle;
    DateTime lastNetwork=DateTime.UtcNow, lastProcesses=DateTime.UtcNow;
    readonly Dictionary<string,(long In,long Out)> network=[];
    readonly Dictionary<int,(long Started,double Seconds)> processTimes=[];
    List<PerformanceCounter> gpuCounters=[];
    DateTime nextGpuRefresh=DateTime.MinValue;
    public Hardware()
    {
        if(OperatingSystem.IsWindows())
        {
            try {computer=WindowsSensorFactory.Create();Status="Windows + LibreHardwareMonitor";}
            catch(Exception e){Paths.Log("sensor-init",e);Status="Windows counters; deep sensors unavailable";}
        }
        else Status="Linux procfs / hwmon / DRM";
    }
    public List<Reading> Sample()
    {
        var list=new List<Reading>();
        void Add(string id,string name,string device,string unit,double? v,string source="OS")=>list.Add(new(id,name,device,unit,v,source));
        if(OperatingSystem.IsWindows())
        {
            if(GetSystemTimes(out var idle,out var kernel,out var user))
            {ulong total=kernel+user-lastKernel-lastUser;double? load=lastKernel>0&&total>0?Math.Clamp(100.0*(total-(idle-lastIdle))/total,0,100):null;Add("cpu.load","CPU utilization",CpuName,"%",load);lastIdle=idle;lastKernel=kernel;lastUser=user;}
            var mem=new MemoryStatus();mem.Length=(uint)Marshal.SizeOf<MemoryStatus>();
            if(GlobalMemoryStatusEx(ref mem)){Add("ram.used","Physical memory used","System","GiB",(mem.TotalPhysical-mem.AvailablePhysical)/1073741824.0);Add("ram.total","Usable physical memory","System","GiB",mem.TotalPhysical/1073741824.0);Add("ram.available","Available memory","System","GiB",mem.AvailablePhysical/1073741824.0);Add("ram.load","Physical memory utilization","System","%",mem.Load);Add("ram.commit","Committed memory","System","GiB",(mem.TotalPageFile-mem.AvailablePageFile)/1073741824.0);}
            if(computer!=null)
            {
                try {list.AddRange(computer.Sample());CpuName=computer.CpuName;GpuName=computer.GpuName;}
                catch(Exception e){Paths.Log("sensor-update",e);Status="Some sensors failed; OS counters active";}
            }
            SampleGpuEngines(list);
        }
        else SampleLinux(list);
        AddStandardAliases(list);
        foreach(var drive in DriveInfo.GetDrives())
        {
            try{if(!HardwareInventory.IsStorageVolume(drive))continue;Add("disk."+drive.Name+".free","Disk available",drive.Name,"GiB",drive.AvailableFreeSpace/1073741824.0);Add("disk."+drive.Name+".size","Disk capacity",drive.Name,"GiB",drive.TotalSize/1073741824.0);Add("disk."+drive.Name+".used","Disk used",drive.Name,"GiB",(drive.TotalSize-drive.TotalFreeSpace)/1073741824.0);}catch{}
        }
        double dt=(DateTime.UtcNow-lastNetwork).TotalSeconds;double rx=0,tx=0;bool has=false;
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if(nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
            try
            {
                var id="nic."+nic.Id;bool up=nic.OperationalStatus==OperationalStatus.Up;
                Add(id+".up","Link connected",nic.Name,"",up?1:0);
                Add(id+".speed","Link capacity",nic.Name,"Mbps",nic.Speed>0?nic.Speed/1e6:null);
                double? down=null,outgoing=null;
                if(up)
                {
                    var s=nic.GetIPStatistics();
                    if(network.TryGetValue(nic.Id,out var p)&&dt>0&&s.BytesReceived>=p.In&&s.BytesSent>=p.Out){down=(s.BytesReceived-p.In)/dt/1048576;outgoing=(s.BytesSent-p.Out)/dt/1048576;rx+=down.Value*1048576;tx+=outgoing.Value*1048576;has=true;}
                    network[nic.Id]=(s.BytesReceived,s.BytesSent);
                }
                else network.Remove(nic.Id);
                Add(id+".rx","Download",nic.Name,"MiB/s",down);Add(id+".tx","Upload",nic.Name,"MiB/s",outgoing);
            }catch{}
        }
        lastNetwork=DateTime.UtcNow;Add("network.rx","Download","Network","MiB/s",has?rx/1048576:null);Add("network.tx","Upload","Network","MiB/s",has?tx/1048576:null);
        if(ProcessSampleTime==default||(DateTime.UtcNow-lastProcesses).TotalSeconds>=5)SampleProcesses();
        foreach(var app in AgentApps)
        {
            var id="agent."+app.App.ToLowerInvariant();
            Add(id+".cpu","App CPU utilization",app.App,"%",app.Cpu,"OS process tree");
            Add(id+".memory","App working set (sum)",app.App,"MiB",app.MemoryMiB,"OS process tree");
            Add(id+".processes","Readable processes",app.App,"",app.Processes.Count,"OS process tree");
        }
        if(DateTime.UtcNow>=nextInventory){Inventory=HardwareInventory.Read(CpuName,GpuName,list.FirstOrDefault(r=>r.Id=="ram.total")?.Value);nextInventory=DateTime.UtcNow.AddMinutes(1);}
        return list;
    }
    void AddStandardAliases(List<Reading> rows)
    {
        Reading? Find(string device,string unit,params string[] names)=>names.Select(n=>rows.FirstOrDefault(r=>r.Device==device&&r.Unit==unit&&r.Value.HasValue&&r.Name.Contains(n,StringComparison.OrdinalIgnoreCase))).FirstOrDefault(r=>r!=null);
        void Alias(string id,string name,Reading? r,string unit,string device){if(rows.All(x=>x.Id!=id))rows.Add(new(id,name,device,unit,r?.Value,r?.Source??"Not exposed"));}
        Alias("gpu.temperature","GPU temperature",Find(GpuName,"°C","GPU Core","GPU Temperature"),"°C",GpuName);
        Alias("cpu.temperature","CPU temperature",Find(CpuName,"°C","Tctl","Package","Core Max","CPU"),"°C",CpuName);
        Alias("gpu.load","GPU utilization",Find(GpuName,"%","GPU Core","GPU D3D 3D","GPU Compute"),"%",GpuName);
        var dedicated=Find(GpuName,"MiB","D3D Dedicated Memory Used","GPU Memory Used");
        var shared=Find(GpuName,"MiB","D3D Shared Memory Used");
        if(!rows.Any(r=>r.Id=="gpu.local"))rows.Add(new("gpu.local","GPU local allocation",GpuName,"GiB",dedicated?.Value/1024,dedicated?.Source??"Not exposed"));
        if(!rows.Any(r=>r.Id=="gpu.shared"))rows.Add(new("gpu.shared","GPU shared allocation",GpuName,"GiB",shared?.Value/1024,shared?.Source??"Not exposed"));
        foreach(var item in new[]{("gpu.local.total","GPU local capacity",new[]{"D3D Dedicated Memory Total","GPU Memory Total"}),("gpu.shared.total","GPU shared limit",new[]{"D3D Shared Memory Total"})})
            if(!rows.Any(r=>r.Id==item.Item1)){var capacity=Find(GpuName,"MiB",item.Item3);rows.Add(new(item.Item1,item.Item2,GpuName,"GiB",capacity?.Value/1024,capacity?.Source??"Not exposed"));}
        Alias("gpu.power","GPU power",Find(GpuName,"W","GPU Package","GPU Core","GPU Power"),"W",GpuName);
    }
    void SampleGpuEngines(List<Reading> rows)
    {
        if(!OperatingSystem.IsWindows())return;
        try
        {
            if(DateTime.UtcNow>=nextGpuRefresh)
            {
                foreach(var c in gpuCounters)c.Dispose();gpuCounters=[];
                var category=new PerformanceCounterCategory("GPU Engine");
                foreach(var name in category.GetInstanceNames().Where(n=>n.Contains("engtype_Compute")||n.Contains("engtype_3D")))
                {var c=new PerformanceCounter("GPU Engine","Utilization Percentage",name,true);c.NextValue();gpuCounters.Add(c);}
                nextGpuRefresh=DateTime.UtcNow.AddSeconds(30);return;
            }
            var engines=new Dictionary<string,double>();
            foreach(var c in gpuCounters){try{var key=c.InstanceName[(c.InstanceName.IndexOf("luid_",StringComparison.Ordinal))..];engines[key]=engines.GetValueOrDefault(key)+Math.Clamp(c.NextValue(),0,100);}catch{}}
            // Same physical engine is shared across process instances; use busiest engine, not sum across engines.
            if(engines.Count>0)rows.Add(new("gpu.engine","Busiest observed graphics/compute engine","Windows GPU engines","%",engines.Values.Max(),"PDH (all adapters)"));
        }catch(Exception e){Paths.Log("gpu-counters",e);nextGpuRefresh=DateTime.UtcNow.AddMinutes(1);}
    }
    void SampleLinux(List<Reading> rows)
    {
        try
        {
            var cpu=File.ReadLines("/proc/stat").First().Split(' ',StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            long total=cpu.Take(8).Sum(),idle=cpu[3]+cpu[4];
            rows.Add(new("cpu.load","CPU utilization","CPU","%",linuxTotal>0&&total>linuxTotal?100.0*(1-(double)(idle-linuxIdle)/(total-linuxTotal)):null,"procfs"));linuxTotal=total;linuxIdle=idle;
            var info=File.ReadLines("/proc/meminfo").Select(l=>l.Split(':')).ToDictionary(x=>x[0],x=>double.Parse(x[1].Trim().Split(' ')[0],CultureInfo.InvariantCulture)/1048576.0);
            rows.Add(new("ram.total","Physical memory","System","GiB",info["MemTotal"],"procfs"));rows.Add(new("ram.available","Available memory","System","GiB",info["MemAvailable"],"procfs"));rows.Add(new("ram.used","Physical memory used","System","GiB",info["MemTotal"]-info["MemAvailable"],"procfs"));rows.Add(new("ram.load","Physical memory utilization","System","%",100*(1-info["MemAvailable"]/info["MemTotal"]),"procfs"));
            CpuName=File.ReadLines("/proc/cpuinfo").FirstOrDefault(l=>l.StartsWith("model name"))?.Split(':')[1].Trim()??"CPU";
            foreach(var dir in Directory.GetDirectories("/sys/class/hwmon"))
            {
                var device=File.Exists(dir+"/name")?File.ReadAllText(dir+"/name").Trim():Path.GetFileName(dir);
                foreach(var file in Directory.GetFiles(dir,"*_input"))
                {
                    try{var prefix=Path.GetFileNameWithoutExtension(file).Replace("_input","");var label=File.Exists(dir+"/"+prefix+"_label")?File.ReadAllText(dir+"/"+prefix+"_label").Trim():prefix;double value=double.Parse(File.ReadAllText(file),CultureInfo.InvariantCulture);string unit="";
                    if(prefix.StartsWith("temp")){value/=1000;unit="°C";}else if(prefix.StartsWith("fan"))unit="RPM";else if(prefix.StartsWith("power")){value/=1e6;unit="W";}else if(prefix.StartsWith("in")){value/=1000;unit="V";}else continue;
                    rows.Add(new("sensor."+device+"."+Path.GetFileName(dir)+"."+prefix,label,device,unit,value,"hwmon"));
                    if(unit=="°C"&&device.Contains("amdgpu")&&!rows.Any(r=>r.Id=="gpu.temperature"))rows.Add(new("gpu.temperature","GPU temperature",device,unit,value,"hwmon"));
                    if(unit=="°C"&&(device.Contains("k10temp")||device.Contains("coretemp"))&&!rows.Any(r=>r.Id=="cpu.temperature"))rows.Add(new("cpu.temperature","CPU temperature",device,unit,value,"hwmon"));
                    }catch{}
                }
            }
            foreach(var dir in Directory.GetDirectories("/sys/class/drm","card*"))
            {
                var dev=dir+"/device";if(!Directory.Exists(dev))continue;
                if(File.Exists(dev+"/gpu_busy_percent")){GpuName=Path.GetFileName(dir)+" GPU";rows.Add(new("gpu.load","GPU utilization",GpuName,"%",double.Parse(File.ReadAllText(dev+"/gpu_busy_percent")),"DRM"));}
                foreach(var pair in new[]{("mem_info_vram_used","gpu.local","GPU local allocation"),("mem_info_gtt_used","gpu.shared","GPU shared allocation"),("mem_info_vram_total","gpu.local.total","GPU local capacity"),("mem_info_gtt_total","gpu.shared.total","GPU shared limit")})
                    if(File.Exists(dev+"/"+pair.Item1))rows.Add(new(pair.Item2,pair.Item3,GpuName,"GiB",double.Parse(File.ReadAllText(dev+"/"+pair.Item1))/1073741824.0,"DRM"));
            }
        }catch(Exception e){Paths.Log("linux-hardware",e);Status="Some Linux counters unavailable";}
    }
    void SampleProcesses()
    {
        var now=DateTime.UtcNow;double elapsed=(now-lastProcesses).TotalSeconds;var rows=new List<AgentProcess>();var seen=new HashSet<int>();var parents=AgentResources.Parents();int unreadable=0;
        foreach(var p in Process.GetProcesses())using(p)
        {
            try
            {
                double t=p.TotalProcessorTime.TotalSeconds;long started=p.StartTime.ToUniversalTime().Ticks;
                double? cpu=AgentResources.CpuDelta(started,t,processTimes.TryGetValue(p.Id,out var previous)?previous:null,elapsed,Environment.ProcessorCount);
                processTimes[p.Id]=(started,t);seen.Add(p.Id);rows.Add(new(p.Id,parents.GetValueOrDefault(p.Id),p.ProcessName,started,cpu,p.WorkingSet64/1048576.0));
            }catch{unreadable++;}
        }
        foreach(var id in processTimes.Keys.Where(id=>!seen.Contains(id)).ToArray())processTimes.Remove(id);
        AgentApps=AgentResources.Group(rows);UnreadableProcesses=unreadable;
        Processes=rows.OrderByDescending(p=>p.Cpu).ThenByDescending(p=>p.MemoryMiB).Take(25).Select(p=>new ProcessInfo(p.Id,p.Name,p.Cpu,p.MemoryMiB)).ToList();lastProcesses=now;ProcessSampleTime=now;
    }
    public void Dispose(){computer?.Dispose();foreach(var c in gpuCounters)c.Dispose();}
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus{public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
}
