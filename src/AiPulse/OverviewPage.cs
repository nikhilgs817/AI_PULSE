using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiPulse;

public sealed partial class MainWindow
{
    int overviewSeconds=900;
    void Navigate(string target){page=target;scroller=null;layoutPage=null;RenderBody();}
    Button OpenPage(string label,string target)
    {
        var button=Btn(label+" ↗");AutomationProperties.SetName(button,"Overview: open "+target);button.Click+=(_,_)=>Navigate(target);return button;
    }
    static Grid Columns(Control a,Control b)
    {
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*")};a.Margin=new Thickness(0,0,6,0);b.Margin=new Thickness(6,0,0,0);grid.Children.Add(a);Grid.SetColumn(b,1);grid.Children.Add(b);return grid;
    }
    Control Overview()
    {
        long now=J.Now,start=now-overviewSeconds;bool fresh=service.Fresh;
        string Live(string id)=>fresh?Format(id):"—";
        var activity=service.Store.CompactActivity(J.DayStart(DateTimeOffset.FromUnixTimeSeconds(now),TimeZoneInfo.Local),now);
        var alerts=service.Store.Alerts().Where(a=>a.Active).ToList();
        var stack=new StackPanel{Spacing=14};
        stack.Children.Add(Heading("Overview",service.HostName+" · "+service.HostPlatform+" · "+(service.CpuName??"Discovering CPU")));
        var status=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16};
        status.Children.Add(Text(fresh?"LIVE":"WAITING / STALE",12,fresh?"#59dfae":"#e8b65f",true));
        status.Children.Add(Text(service.LastSample>0?"Sampled "+DateTimeOffset.FromUnixTimeSeconds(service.LastSample).ToLocalTime().ToString("MMM d HH:mm:ss zzz"):"Starting collectors…",11,"#8ba0bd"));
        status.Children.Add(Text($"{alerts.Count} active alerts · {service.Settings.RetentionDays}-day history",11,alerts.Count>0?"#e8b65f":"#8ba0bd"));stack.Children.Add(status);
        foreach(var alert in alerts.Take(2))stack.Children.Add(Message(alert.Title,alert.Detail));

        var tiles=new UniformGrid{Columns=Bounds.Width-(localService.Settings.SidebarCollapsed?60:174)-52>=900?4:2};
        void Tile(string label,string value,string caption)
        {
            var card=Metric(label,value,caption);card.Margin=new Thickness(0,0,10,10);card.MinHeight=125;card.Padding=new Thickness(15);tiles.Children.Add(card);
        }
        Tile("CPU UTILIZATION",Live("cpu.load"),$"{service.LogicalProcessors} logical processors · whole host");
        Tile("GPU UTILIZATION",Live("gpu.load"),service.GpuName??"Graphics / compute adapter");
        Tile("SYSTEM MEMORY",Live("ram.used"),"of "+Live("ram.total")+" · "+Live("ram.available")+" available");
        Tile("GPU LOCAL MEMORY",Live("gpu.local"),"of "+Live("gpu.local.total")+" reported capacity");
        Tile("CPU TEMPERATURE",Live("cpu.temperature"),service.SensorBridge.Connected?"Deep sensor helper connected":"Exposed CPU sensor · see Sensors for coverage");
        Tile("GPU TEMPERATURE",Live("gpu.temperature"),"GPU core sensor · unavailable shows —");
        Tile("LOADED MODELS",service.IsRemote&&!fresh?"—":service.Ollama.Models.Count.ToString(),$"{Math.Max(0,service.Ollama.Active.Values.Sum())} observed gateway requests active");
        Tile("ACTIVE AGENTS",service.Settings.ReadAgentUsage&&(!service.IsRemote||fresh)?activity.ActiveAgents.ToString("N0"):"—","Sessions active within 5 min · process use below");stack.Children.Add(tiles);

        var range=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=6,LineSpacing=6};range.Children.Add(Text("TREND WINDOW",12,"#8ba0bd",true));
        foreach(var item in new[]{(900,"15 min"),(3600,"1 hour"),(21600,"6 hours"),(86400,"24 hours")})
        {
            var b=Btn(item.Item2);if(overviewSeconds==item.Item1)b.Background=Brush.Parse("#443477");AutomationProperties.SetName(b,"Overview range: "+item.Item2);
            b.Click+=(_,_)=>{overviewSeconds=item.Item1;RenderBody();};range.Children.Add(b);
        }
        stack.Children.Add(range);
        stack.Children.Add(Columns(OverviewChart("CPU / GPU utilization",start,now,"%",100,
            new("CPU",service.Store.History("cpu.load",start,now),"#40d5ed"),new("GPU",service.Store.History(service.Value("gpu.load").HasValue?"gpu.load":"gpu.engine",start,now),"#ad8bfa")),
            OverviewChart("System memory",start,now,"GiB",service.Value("ram.total"),new ChartSeries("RAM used",service.Store.History("ram.used",start,now),"#40d5ed"))));
        stack.Children.Add(Columns(OverviewChart("CPU / GPU temperature",start,now,"°C",null,
            new("CPU",service.Store.History("cpu.temperature",start,now),"#40d5ed"),new("GPU core",service.Store.History("gpu.temperature",start,now),"#ad8bfa")),WifiPanel(NetworkMetrics.From(service.Readings),start,now,fresh)));

        var onPrem=Metric("ON-PREM TOKENS · TODAY",J.Count(activity.OnPrem),"Ollama gateway · input + output since local midnight");
        var tools=Metric("TOOL TOKENS · TODAY",J.Count(activity.Tools),"Codex / Claude Code observations · cache already included");stack.Children.Add(Columns(onPrem,tools));
        stack.Children.Add(Text("Sources are independent observations and can overlap when a tool uses the Ollama gateway. Open Token for the detailed split.",11,"#8ba0bd"));
        stack.Children.Add(Columns(OverviewModels(),OverviewApps()));
        stack.Children.Add(Columns(OverviewStorage(fresh),OverviewHealth()));
        var requests=service.Store.UsageSince(now-86400,"Ollama",5);
        if(requests.Count>0)
        {
            var recent=new StackPanel{Spacing=10};recent.Children.Add(Text("Latest observed model requests",16,null,true));
            recent.Children.Add(Table(["Time","Model","Duration","Decode tok/s","Outcome"],requests.Select(u=>new[]{DateTimeOffset.FromUnixTimeSeconds(u.Time).ToLocalTime().ToString("MMM d HH:mm:ss"),u.Model,u.DurationMs.HasValue?$"{u.DurationMs/1000:0.00}s":"—",u.DecodeRate?.ToString("0.0")??"—",u.Status}),"1.5*,2*,*,*,1.5*"));stack.Children.Add(Card(recent));
        }
        var actions=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=8,LineSpacing=8};foreach(var target in new[]{"Hardware","Sensors","Models","Agents","Token","History","Alerts"})actions.Children.Add(OpenPage(target,target));stack.Children.Add(actions);
        return stack;
    }
    Control OverviewChart(string title,long start,long end,string unit,double? maximum,params ChartSeries[] series)
    {
        var s=new StackPanel{Spacing=6};s.Children.Add(Text(title,15,null,true));
        var legend=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=16};foreach(var item in series)legend.Children.Add(Text("● "+item.Name,11,item.Color));s.Children.Add(legend);
        var chart=new TrendChart{Height=185,Series=series.ToList(),WindowStart=start,WindowEnd=end,Unit=unit,MaxValue=maximum};
        AutomationProperties.SetName(chart,title+" · dated time axis · "+unit+" · "+TimeZoneInfo.Local.Id);s.Children.Add(chart);
        s.Children.Add(Text("Source: "+(service.IsRemote?service.HostName+" Host Agent":"local collector")+" · minute sample means · gaps indicate missing observations",10,"#8ba0bd"));return Card(s);
    }
    record WifiChoice(string Id,string Name){public override string ToString()=>Name;}
    Control WifiPanel(List<NetworkSample> networks,long start,long end,bool fresh)
    {
        var wifi=networks.Where(n=>n.Wireless).ToList();var selected=string.IsNullOrEmpty(service.Settings.WifiInterfaceId)?wifi.FirstOrDefault(n=>n.Connected)??wifi.FirstOrDefault():wifi.FirstOrDefault(n=>n.Id==service.Settings.WifiInterfaceId);
        var panel=new StackPanel{Spacing=8};panel.Children.Add(Text("Wi-Fi speed & traffic",15,null,true));
        var choices=new List<WifiChoice>{new("","Automatic · active Wi-Fi")};choices.AddRange(wifi.Select(n=>new WifiChoice(n.Id,n.Name+(n.Connected?" · connected":" · disconnected"))));
        if(!string.IsNullOrEmpty(service.Settings.WifiInterfaceId)&&choices.All(c=>c.Id!=service.Settings.WifiInterfaceId))choices.Add(new(service.Settings.WifiInterfaceId,"Selected adapter · unavailable"));
        var select=new ComboBox{ItemsSource=choices,SelectedItem=choices.First(c=>c.Id==service.Settings.WifiInterfaceId),HorizontalAlignment=HorizontalAlignment.Stretch,MinWidth=170};AutomationProperties.SetName(select,"Wi-Fi adapter");
        select.SelectionChanged+=(_,_)=>{if(select.SelectedItem is WifiChoice choice&&service.Settings.WifiInterfaceId!=choice.Id){service.Settings.WifiInterfaceId=choice.Id;Paths.Save(localService.Settings);RenderBody();}};panel.Children.Add(select);
        if(selected==null)
        {
            panel.Children.Add(Text("No readable Wi-Fi adapter is available. Wired and other adapter traffic remains in Hardware.",12,"#8ba0bd"));return Card(panel);
        }
        string Rate(double? v)=>fresh&&selected.Connected&&v.HasValue?$"{v.Value*NetworkMetrics.MiBToMegabits:0.00} Mbps":"—";
        panel.Children.Add(Pair("Download",Rate(selected.DownloadMiBs)));panel.Children.Add(Pair("Upload",Rate(selected.UploadMiBs)));
        panel.Children.Add(Text(selected.Name+" · "+(selected.Connected?"connected":"disconnected")+" · link "+(fresh&&selected.Connected&&selected.LinkMbps.HasValue?$"{selected.LinkMbps:0.#} Mbps":"—"),11,"#8ba0bd"));
        List<MetricPoint> Megabits(string suffix)=>service.Store.History(selected.Id+suffix,start,end).Select(p=>new MetricPoint(p.Time,p.Average*NetworkMetrics.MiBToMegabits,p.Minimum*NetworkMetrics.MiBToMegabits,p.Maximum*NetworkMetrics.MiBToMegabits)).ToList();
        var chart=new TrendChart{Height=150,Unit="Mbps",MaxValue=null,WindowStart=start,WindowEnd=end,Series=[new("Download",Megabits(".rx"),"#40d5ed"),new("Upload",Megabits(".tx"),"#ad8bfa")]};AutomationProperties.SetName(chart,"Wi-Fi throughput · dated time axis · Mbps");
        var legend=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16};legend.Children.Add(Text("● Download",11,"#40d5ed"));legend.Children.Add(Text("● Upload",11,"#ad8bfa"));panel.Children.Add(legend);panel.Children.Add(chart);panel.Children.Add(Text("Actual interface traffic · minute means. Link speed is the router connection rate.",10,"#8ba0bd"));return Card(panel);
    }
    Control OverviewModels()
    {
        var s=new StackPanel{Spacing=10};s.Children.Add(Text("Model activity",16,null,true));
        foreach(var status in service.Ollama.Status)s.Children.Add(Text(status.Key+" · "+status.Value,11,status.Value=="Connected"?"#59dfae":"#e8b65f"));
        if(service.Ollama.Models.Count>0)s.Children.Add(Table(["Model / runtime host","State","Memory"],service.Ollama.Models.Take(5).Select(m=>new[]{m.Name+"\n"+m.Host,m.State,J.Size(m.MemoryGiB)}),"2*,*,*"));
        else s.Children.Add(Text("No models are currently reported loaded.",12,"#8ba0bd"));s.Children.Add(OpenPage("All models ("+service.Ollama.Models.Count+")","Models"));return Card(s);
    }
    Control OverviewApps()
    {
        var s=new StackPanel{Spacing=10};s.Children.Add(Text("Agent resource consumption",16,null,true));
        var apps=service.AgentApps;bool fresh=(service.Hardware!=null||service.IsRemote)&&(DateTime.UtcNow-service.ProcessSampleTime).TotalSeconds<=15;
        if(apps.Count>0)s.Children.Add(Table(["App","CPU host %","Working set","Processes"],apps.Select(a=>new[]{a.App,fresh?a.Cpu?.ToString("0.0")??"Warming up":"—",fresh?$"{a.MemoryMiB:N0} MiB":"—",a.Processes.Count.ToString()}),"1.2*,*,1.3*,*"));
        else s.Children.Add(Text("Collecting readable Codex, Claude and Ollama processes…",12,"#8ba0bd"));
        s.Children.Add(Text("Local app totals shared across sessions. Working sets may share pages; protected processes may be missing.",11,"#8ba0bd"));s.Children.Add(OpenPage("Process details","Agents"));return Card(s);
    }
    Control OverviewStorage(bool fresh)
    {
        var s=new StackPanel{Spacing=10};s.Children.Add(Text("Storage & memory pressure",16,null,true));
        foreach(var disk in service.Readings.Where(r=>r.Id.StartsWith("disk.")&&r.Id.EndsWith(".size")).Take(4))
        {
            var prefix=disk.Id[..^5];var used=service.Value(prefix+".used");s.Children.Add(Text(disk.Device+" · "+(fresh?Format(prefix+".used")+" used / "+Format(disk.Id)+" · "+Format(prefix+".free")+" available":"stale / unavailable"),12));
            if(fresh&&disk.Value>0&&used.HasValue)s.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=100*used.Value/disk.Value.Value,Height=5});
        }
        s.Children.Add(Pair("GPU shared allocation",fresh?Format("gpu.shared"):"—"));s.Children.Add(Text("GPU shared memory overlaps system RAM; it is not additional host capacity.",11,"#8ba0bd"));return Card(s);
    }
    Control OverviewHealth()
    {
        var s=new StackPanel{Spacing=10};s.Children.Add(Text("Monitoring health",16,null,true));
        s.Children.Add(Pair("Hardware",service.Health));s.Children.Add(Pair("Ollama gateway",service.GatewayStatus));
        s.Children.Add(Text(service.Settings.ReadAgentUsage?service.AgentStatus:"Local token metadata import disabled",12,"#8ba0bd"));
        s.Children.Add(Text(service.Settings.LastBackupError??(DateTimeOffset.TryParse(service.Settings.LastBackup,out var backup)?"Last verified backup · "+backup.ToLocalTime().ToString("MMM d HH:mm zzz"):"Backup pending"),12,service.Settings.LastBackupError!=null?"#e8b65f":"#8ba0bd"));
        s.Children.Add(Text("Request tokens cover gateway traffic. Local tool metadata coverage depends on supported source records.",11,"#8ba0bd"));return Card(s);
    }
}
