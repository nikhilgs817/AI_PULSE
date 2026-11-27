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
    async void ShowMeasuredExplanation()
    {
        var now=DateTimeOffset.Now;
        long since=J.DayStart(now,TimeZoneInfo.Local),until=now.ToUnixTimeSeconds();
        var tokens=TokenSummary.From(service.Store.TokenBreakdown(since,until),service.Store.CounterTotals(since,until));
        var fresh=service.Fresh;
        var cpu=fresh?Format("cpu.temperature"):"—";
        var temperature=cpu=="—"
            ?(!fresh?"CPU temperature is unavailable while readings are stale or starting.":!service.IsRemote&&OperatingSystem.IsWindows()&&!service.SensorBridge.Connected?"CPU temperature is unavailable. Check deep-sensor access in Sensors.":"CPU temperature is unavailable from the current sensor sources.")
            :"CPU temperature: "+cpu+".";
        var summary=(fresh?$"Current readings: CPU {Format("cpu.load")}, GPU {Format("gpu.load")}, RAM used {Format("ram.used")}.":"Current resource readings are unavailable.")+
            $"\n\n{temperature}\n\nToday: {CompactCount(tokens.LocalRequests)} on-prem request tokens, {CompactCount(tokens.VllmCounters)} vLLM server tokens, {CompactCount(tokens.ToolRequests)} tool tokens. A dash means no usage measurement for that source.";
        var close=Btn("Close");var dialog=new Window{Title="Explain this · "+service.HostName,Width=440,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new StackPanel{Margin=new Thickness(20),Spacing=12,Children={new TextBlock{Text=summary,TextWrapping=TextWrapping.Wrap},close}}};
        close.Click+=(_,_)=>dialog.Close();await dialog.ShowDialog(this);
    }
    void Navigate(string target){page=target;scroller=null;layoutPage=null;RenderBody();}
    Button OpenPage(string label,string target)
    {
        var button=Btn(label+" ↗");AutomationProperties.SetName(button,"Overview: open "+target);button.Click+=(_,_)=>Navigate(target);return button;
    }
    Control Overview()
    {
        overviewSeconds=Math.Min(overviewSeconds,service.Settings.RetentionDays*86400);
        long now=J.Now,start=now-overviewSeconds;bool fresh=service.Fresh;
        var coreTemperature=J.SensorValue(service.Value("gpu.temperature"),"°C");
        var gpuTemperatureId=coreTemperature.HasValue?"gpu.temperature":GpuVrSocSensor?.Id??"gpu.temperature";
        string Live(string id)=>fresh?Format(id):"—";
        long today=J.DayStart(DateTimeOffset.FromUnixTimeSeconds(now),TimeZoneInfo.Local);
        var activity=service.Store.CompactActivity(today,now);
        var tokens=TokenSummary.From(service.Store.TokenBreakdown(today,now),service.Store.CounterTotals(today,now));
        var spawns=service.Store.AgentSpawns(today,now);
        var alerts=service.Store.Alerts().Where(a=>a.Active).ToList();
        var stack=new StackPanel{Spacing=4};

        var tiles=new List<OverviewTile>();
        void Tile(string label,string value,string caption)
        {
            tiles.Add(new(label,label,1,()=>Metric(label,value,caption,MetricLevel(label))));
        }
        string? MetricLevel(string label)=>!fresh?null:label switch
        {
            "CPU UTILIZATION"=>UsageLevel("cpu.load"),
            "GPU UTILIZATION"=>UsageLevel("gpu.load"),
            "SYSTEM MEMORY"=>UsageLevel("ram.used","ram.total"),
            "GPU LOCAL MEMORY"=>UsageLevel("gpu.local","gpu.local.total"),
            "CPU TEMPERATURE"=>TemperatureLevel("cpu.temperature"),
            "GPU TEMPERATURE" when coreTemperature.HasValue=>TemperatureLevel("gpu.temperature"),
            _=>null
        };
        string? TemperatureLevel(string id)=>fresh?Level(service.Value(id),service.Settings.TemperatureWarning-10,service.Settings.TemperatureWarning):null;
        Tile("CPU UTILIZATION",Live("cpu.load"),$"{service.LogicalProcessors} logical processors · whole host");
        Tile("GPU UTILIZATION",Live("gpu.load"),service.GpuName??"Graphics / compute adapter");
        Tile("SYSTEM MEMORY",Live("ram.used"),"of "+Live("ram.total")+" · "+Live("ram.available")+" available");
        Tile("GPU LOCAL MEMORY",Live("gpu.local"),"of "+Live("gpu.local.total")+" reported capacity");
        Tile("CPU TEMPERATURE",Live("cpu.temperature"),service.SensorBridge.Connected?"Deep sensor helper connected":"Exposed CPU sensor · see Sensors for coverage");
        string gpuTemperatureCaption=gpuTemperatureId!="gpu.temperature"?"GPU VR SoC · voltage regulator":
            IsAmdEdgeTemperature?"GPU edge · AMD driver":"GPU core sensor";
        Tile("GPU TEMPERATURE",Live(gpuTemperatureId),gpuTemperatureCaption);
        Tile("LOADED MODELS",service.IsRemote&&!fresh?"—":service.Ollama.Models.Count.ToString(),$"{Math.Max(0,service.Ollama.Active.Values.Sum())} observed gateway requests active");
        Tile("ACTIVE AGENTS",service.Settings.ReadAgentUsage&&(!service.IsRemote||fresh)?activity.ActiveAgents.ToString("N0"):"—","Sessions and named agents active within 5 min");
        Tile("ON-PREM TOKENS · TODAY",CompactCount(tokens.LocalRequests),"Observed model requests · input + output");
        Tile("TOOL TOKENS · TODAY",CompactCount(tokens.ToolRequests),"Codex / Claude Code · cache included");
        Tile("vLLM TOKENS · TODAY",CompactCount(tokens.VllmCounters),"Observed server counters");
        Tile("AGENT SPAWNS · TODAY",spawns.Count==0?"—":J.Count(spawns.Sum(s=>s.Count)),"Distinct reported agent starts");

        tiles.Add(new("utilization","CPU / GPU utilization",2,()=>MetricChart("overview:utilization","CPU / GPU utilization",start,now,"%",100,
            (from,to)=>[new("CPU",service.Store.History("cpu.load",from,to),"#40d5ed"),GpuUtilizationSeries(from,to)])));
        tiles.Add(new("memory","System memory trend",2,()=>MetricChart("overview:memory","System memory",start,now,"GiB",service.Value("ram.total"),
            (from,to)=>[new("RAM used",service.Store.History("ram.used",from,to),"#40d5ed")])));
        tiles.Add(new("temperature","CPU / GPU temperature",2,()=>MetricChart("overview:temperature","CPU / GPU temperature",start,now,"°C",null,
            (from,to)=>TemperatureSeries(from,to,includeCpu:true))));
        tiles.Add(new("network","Wi-Fi speed & traffic",2,()=>WifiPanel(NetworkMetrics.From(service.Readings),start,now,fresh)));
        tiles.Add(new("models","Models",2,OverviewModels));
        tiles.Add(new("apps","Agent resource consumption",2,OverviewApps));
        tiles.Add(new("storage","Storage & memory pressure",2,()=>OverviewStorage(fresh)));
        tiles.Add(new("health","Monitoring health",2,OverviewHealth));
        tiles.Add(new("alerts","Active alerts",2,()=>Card(new StackPanel{Spacing=8,Children={
            Text("Active alerts",16,null,true),
            Text(alerts.Count==0?"No active alerts":string.Join("\n",alerts.Select(a=>a.Title))),
            OpenPage("Open alerts","Alerts")
        }}),Optional:true));
        tiles.Add(new("network-details","Network adapters",2,()=>Card(new StackPanel{Spacing=8,Children={
            Text("Network adapters",16,null,true),
            Text(string.Join("\n",NetworkMetrics.From(service.Readings).Select(n=>n.Name).DefaultIfEmpty("No network adapters reported."))),
            OpenPage("Open hardware","Hardware")
        }}),Optional:true));
        tiles.Add(new("requests","Latest observed model requests",4,()=>
        {
            var requests=service.Store.UsageSince(now-86400,"Ollama",5);
            var recent=new StackPanel{Spacing=10};recent.Children.Add(Text("Latest observed model requests",16,null,true));
            if(requests.Count==0)recent.Children.Add(Text("No Ollama requests observed in the last 24 hours."));
            else recent.Children.Add(Table(["Time","Model","Duration","Decode tok/s","Outcome"],requests.Select(u=>new[]{DateTimeOffset.FromUnixTimeSeconds(u.Time).ToLocalTime().ToString("MMM d HH:mm:ss"),u.Model,u.DurationMs.HasValue?$"{u.DurationMs/1000:0.00}s":"—",u.DecodeRate?.ToString("0.0")??"—",u.Status}),"1.5*,2*,*,*,1.5*"));
            return Card(recent);
        },Optional:true));
        foreach(var widget in localService.Settings.CustomMetricWidgets??[])
            tiles.Add(new(widget.Id,widget.Label,1,()=>Metric(widget.Label,Live(widget.MetricId),"Live sensor · "+widget.MetricId,
                widget.MetricId.EndsWith(".temperature",StringComparison.Ordinal)?TemperatureLevel(widget.MetricId):
                fresh&&widget.MetricId.EndsWith(".load",StringComparison.Ordinal)?Level(service.Value(widget.MetricId)):null),Optional:true));
        stack.Children.Add(WidgetLayout(tiles));
        var actions=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=8,LineSpacing=8};foreach(var target in new[]{"Hardware","Sensors","Models","Agents","Token","History","Alerts"})actions.Children.Add(OpenPage(target,target));stack.Children.Add(actions);
        return stack;
    }
    internal static string? Level(double? value,double amber=75,double red=90)=>
        value is double v&&double.IsFinite(v)?v>=red?"#ff7187":v>=amber?"#e8b65f":"#59dfae":null;
    Control OverviewFooterActions()
    {
        var explain=Btn("Explain");
        explain.FontSize=10;explain.Padding=new Thickness(7,2);explain.MinHeight=23;
        AutomationProperties.SetName(explain,"Explain this host");
        explain.Click+=(_,_)=>ShowMeasuredExplanation();
        return explain;
    }
    sealed record ChartRange(int Seconds,string Label)
    {
        public override string ToString()=>Label;
    }
    static readonly ChartRange[] QuickChartRanges =
        [new(900,"15 min"),new(3600,"1 hour"),new(21600,"6 hours"),new(86400,"24 hours"),new(7*86400,"7 days")];

    Control ChartRangePicker(string name,int selectedSeconds,Action<int> apply)
    {
        int retention=service.Settings.RetentionDays;
        var ranges=QuickChartRanges.ToList();
        ranges.AddRange(new[]{30,90,180,365}.Where(days=>days<=retention).Append(retention).Where(days=>days>7).Distinct()
            .Select(days=>new ChartRange(days*86400,$"{days} days")));
        if(ranges.All(r=>r.Seconds!=selectedSeconds))ranges.Add(new(selectedSeconds,$"{selectedSeconds/86400} days"));
        ranges.Sort((a,b)=>a.Seconds.CompareTo(b.Seconds));
        var picker=RangeDropdown(name,ranges,selectedSeconds,apply);
        return new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,Children={
            new TextBlock{Text="Chart range",FontSize=11,VerticalAlignment=VerticalAlignment.Center},picker}};
    }

    ComboBox RangeDropdown(string name,IReadOnlyList<ChartRange> ranges,int selectedSeconds,Action<int> apply)
    {
        var picker=new ComboBox{ItemsSource=ranges,SelectedItem=ranges.First(r=>r.Seconds==selectedSeconds),
            FontSize=11,MinHeight=26,VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(picker,name);
        picker.SelectionChanged+=(_,_)=>
        {
            if(picker.SelectedItem is ChartRange range&&range.Seconds!=selectedSeconds)
            {
                apply(range.Seconds);
                RenderBody();
            }
        };
        return picker;
    }

    Control GraphHeader(string id,string title,long pageStart,long end,out long start)
    {
        var prefs=localService.Settings;
        int selected=prefs.GraphRangeOverrides?.GetValueOrDefault(id)??0;
        if(!QuickChartRanges.Any(range=>range.Seconds==selected))selected=0;
        start=selected==0?pageStart:end-selected;

        var label=Text(title,15,null,true);
        if(page=="History"?editingHistory:editingOverview)return label;

        long pageSeconds=end-pageStart;
        var inherited=QuickChartRanges.FirstOrDefault(r=>r.Seconds==pageSeconds)?.Label??$"{pageSeconds/86400} days";
        var ranges=new List<ChartRange>{new(0,"Page · "+inherited)};
        ranges.AddRange(QuickChartRanges);
        var picker=RangeDropdown(title+" graph range",ranges,selected,seconds=>
        {
            var overrides=prefs.GraphRangeOverrides??=[];
            if(seconds==0)overrides.Remove(id);
            else overrides[id]=seconds;
            Paths.Save(prefs);
        });
        ToolTip.SetTip(picker,"Time range for this graph");
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=6,Children={label}};
        label.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(picker,1);header.Children.Add(picker);
        return header;
    }
    ChartSeries GpuUtilizationSeries(long start,long end)=>
        new("GPU",service.Store.History("gpu.load",start,end,"gpu.engine"),"#ad8bfa");

    ChartSeries[] TemperatureSeries(long start,long end,bool includeCpu)
    {
        var series=new List<ChartSeries>();
        if(includeCpu)series.Add(new("CPU",service.Store.History("cpu.temperature",start,end),"#40d5ed"));
        var core=service.Store.History("gpu.temperature",start,end);
        if(core.Count>0||GpuVrSocSensor is null)series.Add(new(IsAmdEdgeTemperature?"GPU edge":"GPU core",core,"#ad8bfa"));
        if(GpuVrSocSensor is {} soc)series.Add(new("GPU VR SoC",service.Store.History(soc.Id,start,end),"#e8b65f"));
        return series.ToArray();
    }
    bool IsAmdEdgeTemperature=>service.Readings.Any(r=>r.Id=="gpu.temperature"&&r.Source=="AMD ADLX");

    Control MetricChart(string id,string title,long pageStart,long end,string unit,double? maximum,
        Func<long,long,ChartSeries[]> readSeries)
    {
        var s=new StackPanel{Spacing=6};s.Children.Add(GraphHeader(id,title,pageStart,end,out long start));
        var series=readSeries(start,end);
        var legend=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=16};foreach(var item in series)legend.Children.Add(Text("● "+item.Name,11,item.Color));s.Children.Add(legend);
        long bucket=Storage.HistoryBucket(start,end);
        var chart=new TrendChart{Height=185,Series=series.ToList(),WindowStart=start,WindowEnd=end,Unit=unit,MaxValue=maximum,ExpectedIntervalSeconds=bucket};
        AutomationProperties.SetName(chart,title+" · dated time axis · "+unit+" · "+TimeZoneInfo.Local.Id);s.Children.Add(chart);
        s.Children.Add(Text((service.IsRemote?service.HostName+" Host Agent":"Local collector")+$" · {bucket/60}-minute averages · gaps indicate missing observations",10,"#8ba0bd"));return Card(s);
    }
    record WifiChoice(string Id,string Name){public override string ToString()=>Name;}
    Control WifiPanel(List<NetworkSample> networks,long pageStart,long end,bool fresh)
    {
        var wifi=networks.Where(n=>n.Wireless).ToList();var selected=string.IsNullOrEmpty(service.Settings.WifiInterfaceId)?wifi.FirstOrDefault(n=>n.Connected)??wifi.FirstOrDefault():wifi.FirstOrDefault(n=>n.Id==service.Settings.WifiInterfaceId);
        var panel=new StackPanel{Spacing=8};
        long start=pageStart;
        panel.Children.Add(selected is null?Text("Wi-Fi speed & traffic",15,null,true)
            :GraphHeader("overview:network","Wi-Fi speed & traffic",pageStart,end,out start));
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
        long bucket=Storage.HistoryBucket(start,end);
        var chart=new TrendChart{Height=150,Unit="Mbps",MaxValue=null,WindowStart=start,WindowEnd=end,ExpectedIntervalSeconds=bucket,Series=[new("Download",Megabits(".rx"),"#40d5ed"),new("Upload",Megabits(".tx"),"#ad8bfa")]};AutomationProperties.SetName(chart,"Wi-Fi throughput · dated time axis · Mbps");
        var legend=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16};legend.Children.Add(Text("● Download",11,"#40d5ed"));legend.Children.Add(Text("● Upload",11,"#ad8bfa"));panel.Children.Add(legend);panel.Children.Add(chart);panel.Children.Add(Text($"Actual interface traffic · {bucket/60}-minute averages. Link speed is the router connection rate.",10,"#8ba0bd"));return Card(panel);
    }
    Control OverviewModels()
    {
        var s=new StackPanel{Spacing=10};s.Children.Add(Text("Model activity",16,null,true));
        foreach(var status in service.Ollama.Status)s.Children.Add(Text(status.Key+" · "+status.Value,11,status.Value=="Connected"?"#59dfae":"#e8b65f"));
        if(service.Ollama.Models.Count>0)s.Children.Add(Table(["Model / runtime host","State","Memory"],service.Ollama.Models.Take(5).Select(m=>new[]{m.Name+"\n"+ModelHost(m),m.State,J.Size(m.MemoryGiB)}),"2*,*,*"));
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
