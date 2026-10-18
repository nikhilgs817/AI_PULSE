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

namespace AiPulse;

public sealed class MainWindow : Window
{
    readonly MonitorService service;
    readonly Grid shell=new(){RowDefinitions=new RowDefinitions("64,*,34")};
    readonly ContentControl body=new();
    readonly TextBlock footer=Text("Starting collectors…",11,"#91a4be");
    readonly TextBlock modeLabel=Text("",11,"#8ca0bd");
    readonly Button modeButton=Btn("Expand ↗");
    readonly Button pinButton=Btn("Pinned");
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    string page="Overview";
    string notice="";
    bool firstPosition=true;
    int updates;
    ScrollViewer? scroller;
    Vector compactOffset;
    bool rebuilding;
    static readonly IBrush Canvas=Brush.Parse("#0b111c"),Panel=Brush.Parse("#121c2b"),Line=Brush.Parse("#26344a");
    public MainWindow(MonitorService monitor)
    {
        service=monitor;Title="AI Pulse";Background=Canvas;Foreground=Brush.Parse("#e9effa");FontFamily="Segoe UI";
        int pi=Array.IndexOf(Program.Arguments,"--page");if(pi>=0&&Program.Arguments.Length>pi+1)page=Program.Arguments[pi+1];
        MinWidth=520;MinHeight=330;Width=service.Settings.Compact?service.Settings.CompactWidth:1280;Height=service.Settings.Compact?service.Settings.CompactHeight:820;
        Topmost=service.Settings.Pinned;ExtendClientAreaToDecorationsHint=true;WindowDecorations=WindowDecorations.BorderOnly;ExtendClientAreaTitleBarHeightHint=0;
        try{Icon=new WindowIcon(AssetLoader.Open(new Uri("avares://AiPulse/Assets/pulse.ico")));}catch{}
        var title=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        var brand=new Border{Width=240,Height=42,HorizontalAlignment=HorizontalAlignment.Left,ClipToBounds=true,Child=new Image{Source=new Bitmap(AssetLoader.Open(new Uri("avares://AiPulse/Assets/wordmark.png"))),Stretch=Stretch.UniformToFill}};
        Avalonia.Automation.AutomationProperties.SetName(brand,"AIPULSE · hardware, models and agents");
        brand.PointerPressed+=(_,e)=>{if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)BeginMoveDrag(e);};title.Children.Add(brand);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,VerticalAlignment=VerticalAlignment.Center};
        pinButton.Click+=(_,_)=>{Topmost=!Topmost;service.Settings.Pinned=Topmost;Paths.Save(service.Settings);UpdateModeLabel();};modeButton.Click+=(_,_)=>ToggleMode();controls.Children.Add(pinButton);controls.Children.Add(modeButton);
        var min=Btn("—");min.Click+=(_,_)=>WindowState=WindowState.Minimized;controls.Children.Add(min);var close=Btn("×");close.Click+=(_,_)=>Close();controls.Children.Add(close);Grid.SetColumn(controls,1);title.Children.Add(controls);
        shell.Children.Add(new Border{Child=title,Padding=new Thickness(18,10),BorderBrush=Line,BorderThickness=new Thickness(0,0,0,1)});
        Grid.SetRow(body,1);shell.Children.Add(body);Grid.SetRow(footer,2);footer.Margin=new Thickness(18,7);shell.Children.Add(footer);Content=shell;
        Closing+=(_,e)=>{if(Application.Current is App a&&!a.Quitting){SavePosition();if(OperatingSystem.IsWindows()){e.Cancel=true;Hide();}else{a.Quitting=true;(a.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}}};
        Opened+=async(_,_)=>
        {
            if(firstPosition){firstPosition=false;var screen=Screens.Primary??Screens.All.FirstOrDefault();if(screen!=null){var area=screen.WorkingArea;int x=(int)(service.Settings.X??(area.Right-Width*screen.Scaling-20)),y=(int)(service.Settings.Y??(area.Bottom-Height*screen.Scaling-48));Position=new PixelPoint(Math.Clamp(x,area.X,Math.Max(area.X,area.Right-(int)(Width*screen.Scaling))),Math.Clamp(y,area.Y,Math.Max(area.Y,area.Bottom-(int)(Height*screen.Scaling))));}}
            if(Program.Arguments.Contains("--enable-sensors"))service.SensorBridge.Enable();
            if(Program.Arguments.Contains("--capture")){await Task.Delay(12000);Capture();}
            int exitIndex=Array.IndexOf(Program.Arguments,"--exit-after");if(exitIndex>=0&&Program.Arguments.Length>exitIndex+1&&int.TryParse(Program.Arguments[exitIndex+1],out int seconds)){await Task.Delay(Math.Clamp(seconds,1,3600)*1000);if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}}
        };
        KeyDown+=(_,e)=>{if(e.Key==Key.D&&e.KeyModifiers.HasFlag(KeyModifiers.Control))ToggleMode();if(e.Key==Key.Q&&e.KeyModifiers.HasFlag(KeyModifiers.Control))Quit();};
        timer.Tick+=(_,_)=>Refresh();timer.Start();UpdateModeLabel();Refresh();
    }
    void SavePosition(){service.Settings.X=Position.X;service.Settings.Y=Position.Y;if(service.Settings.Compact){service.Settings.CompactWidth=Width;service.Settings.CompactHeight=Height;}Paths.Save(service.Settings);}
    void Quit(){SavePosition();if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}}
    void UpdateModeLabel(){modeLabel.Text=service.Settings.Compact?"YOUR AI · AT A GLANCE":"HARDWARE / MODELS / AGENTS";modeButton.Content=service.Settings.Compact?"Expand ↗":"Compact ↙";pinButton.Content=Topmost?"Pinned":"Pin";}
    public void ToggleMode()
    {
        SavePosition();service.Settings.Compact=!service.Settings.Compact;Width=service.Settings.Compact?service.Settings.CompactWidth:1280;Height=service.Settings.Compact?service.Settings.CompactHeight:820;Paths.Save(service.Settings);UpdateModeLabel();RenderBody();
        var screen=Screens.ScreenFromWindow(this)??Screens.Primary;if(screen!=null){var area=screen.WorkingArea;Position=new PixelPoint(Math.Clamp(Position.X,area.X,Math.Max(area.X,area.Right-(int)(Width*screen.Scaling))),Math.Clamp(Position.Y,area.Y,Math.Max(area.Y,area.Bottom-(int)(Height*screen.Scaling))));}
    }
    void Refresh()
    {
        if(!IsVisible&&updates++>0)return;
        var alerts=service.Store.Alerts().Count(a=>a.Active);string fresh=service.LastSample==0?"Starting":J.Now-service.LastSample>8?"STALE readings":"Live · 2s";
        footer.Text=$"{fresh}   ·   {alerts} alerts   ·   90-day history   ·   "+(service.Settings.LastBackupError!=null?"Backup needs attention":service.Settings.LastBackup!=null?"Backup verified":"Backup pending");
        if(page!="Settings"||service.Settings.Compact)RenderBody();
    }
    void RenderBody()
    {
        if(rebuilding)return;rebuilding=true;var offset=scroller?.Offset??default;
        try
        {
            if(service.Settings.Compact){body.Content=Compact();return;}
            var layout=new Grid{ColumnDefinitions=new ColumnDefinitions("174,*")};var nav=new StackPanel{Spacing=8,Margin=new Thickness(12,20)};
            foreach(var item in new[]{"Overview","Hardware","Models","Agents","History","Alerts","Settings"})
            {var b=Btn(item);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Padding=new Thickness(16,12);if(item==page)b.Background=Brush.Parse("#443477");b.Click+=(_,_)=>{page=item;scroller=null;RenderBody();};nav.Children.Add(b);}
            var host=Text("This PC\n"+(OperatingSystem.IsWindows()?"Windows":"Ubuntu / Linux")+" · v0.1.0",11,"#7f93af");host.Margin=new Thickness(12,35,0,0);nav.Children.Add(host);
            layout.Children.Add(new Border{Child=nav,BorderBrush=Line,BorderThickness=new Thickness(0,0,1,0)});
            Control content=page switch{"Overview"=>Overview(),"Hardware"=>HardwarePage(),"Models"=>ModelsPage(),"Agents"=>AgentsPage(),"History"=>HistoryPage(),"Alerts"=>AlertsPage(),_=>SettingsPage()};
            scroller=new ScrollViewer{Content=content,Margin=new Thickness(26,20),HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetColumn(scroller,1);layout.Children.Add(scroller);body.Content=layout;scroller.Offset=offset;
        }finally{rebuilding=false;}
    }
    Control Compact()
    {
        var grid=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,*,Auto"),Margin=new Thickness(16,12,16,0)};
        var metrics=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*,*,*,*")};
        string[] labels=["GPU TEMP","GPU LOAD","GPU LOCAL","RAM USED","CPU TEMP"];
        string[] ids=["gpu.temperature","gpu.load","gpu.local","ram.used","cpu.temperature"];
        if(service.Value("gpu.temperature")==null&&service.Readings.FirstOrDefault(r=>r.Name=="GPU VR SoC") is {Value:not null} soc){labels[0]="GPU VR SOC";ids[0]=soc.Id;}
        for(int i=0;i<ids.Length;i++){var cell=new StackPanel{Spacing=8,Margin=new Thickness(8,8,2,18)};cell.Children.Add(Text(labels[i],10,"#8ea3c0"));cell.Children.Add(Text(Format(ids[i]),21,"#e9f0ff",true));Grid.SetColumn(cell,i);metrics.Children.Add(cell);}grid.Children.Add(metrics);
        var summary=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(8,5,8,10)};summary.Children.Add(Text("LOADED MODELS",11,"#a1b4ce"));var count=Text(service.Ollama.Models.Count+" loaded · all hosts",11,"#7f93af");Grid.SetColumn(count,1);summary.Children.Add(count);Grid.SetRow(summary,1);grid.Children.Add(summary);
        var list=new StackPanel{Spacing=6};
        if(service.Ollama.Models.Count==0)list.Children.Add(Message(service.Ollama.Status.GetValueOrDefault("This PC")=="Connected"?"Ollama is ready. No models loaded.":"Waiting for Ollama…","Loaded models will appear here automatically."));
        var rates=service.Store.UsageSince(J.Now-3600).Where(u=>u.Source=="Ollama"&&u.DecodeRate.HasValue).GroupBy(u=>u.Model).ToDictionary(g=>g.Key,g=>g.First());
        foreach(var m in service.Ollama.Models.OrderBy(m=>m.Host).ThenBy(m=>m.Name))
        {
            var row=new Grid{ColumnDefinitions=new ColumnDefinitions("*,100,90,90"),Margin=new Thickness(12,10)};var name=new StackPanel{Spacing=3};name.Children.Add(Text(m.Name,13,"#e6edf9",true));name.Children.Add(Text(m.Host+" · Ollama",10,"#7f93af"));row.Children.Add(name);
            var state=Text(m.State,11,m.State=="Generating"?"#59dfae":"#9aabc3");Grid.SetColumn(state,1);row.Children.Add(state);
            var r=Text(m.Host=="This PC"&&rates.TryGetValue(m.Name,out var u)?$"{u.DecodeRate:0.0} t/s\nlast request":"— tok/s",11,"#55cee5");Grid.SetColumn(r,2);row.Children.Add(r);
            var memory=Text(J.Size(m.MemoryGiB),11,"#c3a8ff");Grid.SetColumn(memory,3);row.Children.Add(memory);
            var card=new Border{Child=row,Background=Panel,CornerRadius=new CornerRadius(7)};card.PointerPressed+=(_,_)=>{page="Models";if(service.Settings.Compact)ToggleMode();else RenderBody();};list.Children.Add(card);
        }
        var sessions=service.Store.Sessions().Where(s=>J.Now-s.LastSeen<3600).Take(8).ToList();
        if(sessions.Count>0){list.Children.Add(Text("AGENT SESSIONS · RECENT",10,"#8ea3c0"));foreach(var s in sessions){var row=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(8,5)};row.Children.Add(Text(s.Source+" · "+s.Model,11));var usage=Text(J.Count(s.Input+s.Output)+" tokens",11,"#a68aff");Grid.SetColumn(usage,1);row.Children.Add(usage);list.Children.Add(row);}}
        var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Offset=compactOffset};scroll.ScrollChanged+=(_,_)=>compactOffset=scroll.Offset;Grid.SetRow(scroll,2);grid.Children.Add(scroll);
        var bottom=Text("Hardware: this PC · Model memory: runtime-reported · Ctrl+D expands",10,"#7186a2");bottom.Margin=new Thickness(8,10);Grid.SetRow(bottom,3);grid.Children.Add(bottom);return grid;
    }
    string Format(string id)
    {
        double? v=service.Value(id);if(id=="gpu.load"&&!v.HasValue)v=service.Value("gpu.engine");
        if(!v.HasValue)return "—";return id.Contains("temperature")?$"{v:0}°C":id.Contains("load")?$"{v:0}%":$"{v:0.0} GiB";
    }
    Control Overview()
    {
        var stack=new StackPanel{Spacing=18};stack.Children.Add(Heading("Overview",(service.Hardware?.GpuName??"Discovering GPU")+" · "+(service.Hardware?.CpuName??"Discovering CPU")));
        var stats=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*,*,*")};
        var total=service.Store.Totals(J.Now-86400);
        var cards=new[]{Metric("GPU UTILIZATION",Format("gpu.load"),"Device / busiest observed engine"),Metric("GPU TEMPERATURE",Format("gpu.temperature"),"Unavailable sensors show —"),Metric("SYSTEM MEMORY",Format("ram.used"),"of "+Format("ram.total")),Metric("OBSERVED TOKENS · 24H",J.Count(total.Input+total.Output),J.Count(total.Cached)+" cached input · included")};
        for(int i=0;i<cards.Length;i++){Grid.SetColumn(cards[i],i);cards[i].Margin=new Thickness(i==0?0:10,0,0,0);stats.Children.Add(cards[i]);}stack.Children.Add(stats);
        var mid=new Grid{ColumnDefinitions=new ColumnDefinitions("2*,*")};var trend=new StackPanel{Spacing=12};trend.Children.Add(Text("GPU utilization",15,null,true));var chart=new TrendChart{Height=205,Points=service.Store.History(service.Value("gpu.load").HasValue?"gpu.load":"gpu.engine",J.Now-900)};trend.Children.Add(chart);trend.Children.Add(Text("Last 15 min · minute means · local collector",10,"#7b90ad"));mid.Children.Add(Card(trend));
        var memory=new StackPanel{Spacing=18};memory.Children.Add(Text("Unified memory",15,null,true));memory.Children.Add(Pair("GPU local",Format("gpu.local")));memory.Children.Add(Pair("GPU shared",Format("gpu.shared")));memory.Children.Add(Pair("System available",Format("ram.available")));memory.Children.Add(Text("GPU shared allocations overlap system RAM.\nThese figures must not be added together.",11,"#8a9eba"));var memCard=Card(memory);memCard.Margin=new Thickness(12,0,0,0);Grid.SetColumn(memCard,1);mid.Children.Add(memCard);stack.Children.Add(mid);
        stack.Children.Add(ModelsList(false));
        stack.Children.Add(Message("Usage coverage",service.Agents.Status+"\nOllama request tokens: gateway traffic only. Desktop Chat/Cowork may not expose usage."));
        return stack;
    }
    Control HardwarePage()
    {
        var stack=new StackPanel{Spacing=16};stack.Children.Add(Heading("Hardware",service.Health));
        stack.Children.Add(Message("Sensor availability",service.Value("cpu.temperature")==null?"CPU temperature is not exposed in the current permission/driver state. GPU and other available readings remain live.":"Sensor readings come from the installed hardware interfaces. Missing values stay unavailable."));
        stack.Children.Add(Text("Sensor inventory",16,null,true));
        var rows=service.Readings.OrderBy(r=>r.Device).ThenBy(r=>r.Name).Select(r=>new[]{r.Device,r.Name,r.Value?.ToString("0.##")??"Unavailable",r.Unit,r.Source});stack.Children.Add(Table(["Device","Sensor","Value","Unit","Source"],rows,"1.4*,2*,*,70,1.5*"));
        stack.Children.Add(Text("Top processes",16,null,true));stack.Children.Add(Table(["Process","PID","CPU (host %)","Working set MiB"],(service.Hardware?.Processes??[]).Select(p=>new[]{p.Name,p.Id.ToString(),p.Cpu.ToString("0.0"),p.MemoryMiB.ToString("N0")}),"2*,*,*,*"));return stack;
    }
    Control ModelsPage()
    {
        var stack=new StackPanel{Spacing=18};stack.Children.Add(Heading("Models","Installed is different from loaded. Generating is confirmed only for observed requests."));stack.Children.Add(ModelsList(true));
        stack.Children.Add(Text("Recent observed requests",16,null,true));stack.Children.Add(Table(["Model","Input","Output","Decode tok/s","Duration","Outcome"],service.Store.UsageSince(J.Now-86400).Where(u=>u.Source=="Ollama").Take(100).Select(u=>new[]{u.Model,u.Status!="Completed"?"—":J.Count(u.Input),u.Status!="Completed"?"—":J.Count(u.Output),u.DecodeRate?.ToString("0.0")??"—",u.DurationMs.HasValue?$"{u.DurationMs/1000:0.0}s":"—",u.Status}),"2*,*,*,*,*,2*"));return stack;
    }
    Control ModelsList(bool inventory)
    {
        var stack=new StackPanel{Spacing=12};stack.Children.Add(Text("Loaded models · "+service.Ollama.Models.Count,16,null,true));
        foreach(var status in service.Ollama.Status)stack.Children.Add(Text(status.Key+"  ·  "+status.Value,11,status.Value=="Connected"?"#59dfae":"#e8b65f"));
        if(service.Ollama.Models.Count>0)stack.Children.Add(Table(["Model / host","State","Model memory","GPU-reported","Context"],service.Ollama.Models.Select(m=>new[]{m.Name+"\n"+m.Host,m.State,J.Size(m.MemoryGiB),J.Size(m.GpuGiB),m.Context>0?J.Count(m.Context):"—"}),"2.2*,*,*,*,*"));
        else stack.Children.Add(Text("No models are loaded. Start an Ollama request to see residency here.",13,"#8da1bd"));
        if(inventory){stack.Children.Add(Text("Installed inventory",15,null,true));stack.Children.Add(Table(["Model","Host","Quantization","Disk size"],service.Ollama.Installed.Select(m=>new[]{m.Name,m.Host,m.Quantization,J.Size(m.MemoryGiB)}),"2*,*,*,*"));}
        return Card(stack);
    }
    Control AgentsPage()
    {
        var stack=new StackPanel{Spacing=16};stack.Children.Add(Heading("Agents",service.Agents.Status));stack.Children.Add(Message("Metadata only","Input/output/cache tokens are read from supported local session records. Prompts, replies and tool output are not stored. Quiet sessions are not assumed completed."));
        var sessions=service.Store.Sessions(90);stack.Children.Add(Table(["Source / session","Model","State","Input","Output","Last activity"],sessions.Select(s=>new[]{s.Source+"\n"+s.Id[..Math.Min(12,s.Id.Length)],s.Model,J.Now-s.LastSeen>300&&s.State is "Working" or "Recent activity"?"Quiet / unknown":s.State,J.Count(s.Input),J.Count(s.Output),DateTimeOffset.FromUnixTimeSeconds(s.LastSeen).ToLocalTime().ToString("MMM d HH:mm")}),"1.5*,1.7*,1.3*,*,*,1.3*"));
        if(sessions.Count==0)stack.Children.Add(Text("No supported sessions have been imported yet.",13,"#8da1bd"));return stack;
    }
    int historyDays=1;
    Control HistoryPage()
    {
        var stack=new StackPanel{Spacing=18};stack.Children.Add(Heading("History","7-day detailed readings · 90-day minute summaries and usage records"));var options=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(int days in new[]{1,7,30,90}){var b=Btn(days+" day"+(days==1?"":"s"));if(days==historyDays)b.Background=Brush.Parse("#443477");b.Click+=(_,_)=>{historyDays=days;RenderBody();};options.Children.Add(b);}stack.Children.Add(options);
        long since=J.Now-historyDays*86400;var totals=service.Store.Totals(since);stack.Children.Add(Metric("OBSERVED TOKENS",J.Count(totals.Input+totals.Output),$"Input {J.Count(totals.Input)} · output {J.Count(totals.Output)} · cached input {J.Count(totals.Cached)} (subset)"));
        foreach(var metric in new[]{("gpu.temperature","GPU temperature","°C"),("cpu.load","CPU utilization","%"),("ram.used","System memory used","GiB")}){var points=service.Store.History(metric.Item1,since);var chartStack=new StackPanel{Spacing=8};chartStack.Children.Add(Text(metric.Item2,15,null,true));chartStack.Children.Add(new TrendChart{Height=150,Points=points,Unit=metric.Item3,MaxValue=metric.Item3=="%"?100:null});chartStack.Children.Add(Text("Source: local collector · sample means; gaps indicate missing observations",10,"#7f93af"));stack.Children.Add(Card(chartStack));}
        var export=Btn("Export usage CSV");export.Click+=async(_,_)=>{export.IsEnabled=false;try{notice="Saved "+await Task.Run(service.Export);}catch(Exception e){Paths.Log("export",e);notice="Export failed. Check available space and folder access.";}RenderBody();};stack.Children.Add(export);if(notice!="")stack.Children.Add(Text(notice,11,"#59dfae"));return stack;
    }
    Control AlertsPage()
    {
        var stack=new StackPanel{Spacing=14};stack.Children.Add(Heading("Alerts","Sustained thresholds · no automatic model termination"));var alerts=service.Store.Alerts();foreach(var a in alerts.Where(a=>a.Active))stack.Children.Add(Message(a.Title,a.Detail));if(!alerts.Any(a=>a.Active))stack.Children.Add(Message("No active alerts","Monitoring temperature, memory pressure, storage and backup health."));stack.Children.Add(Text("Recent checks",15,null,true));stack.Children.Add(Table(["Check","State","Changed"],alerts.Select(a=>new[]{a.Title,a.Active?"Attention":"Clear",DateTimeOffset.FromUnixTimeSeconds(a.Time).ToLocalTime().ToString("MMM d HH:mm")}),"2*,*,*"));return stack;
    }
    Control SettingsPage()
    {
        var stack=new StackPanel{Spacing=15};stack.Children.Add(Heading("Settings","Everything stays on this machine unless you connect a remote model endpoint."));
        if(OperatingSystem.IsWindows()){var sensors=Btn("Enable deep sensors for this session");sensors.Click+=(_,_)=>{service.SensorBridge.Enable();notice=service.SensorBridge.Status;RenderBody();};stack.Children.Add(sensors);stack.Children.Add(Text(service.SensorBridge.Status+" · A Windows administrator prompt opens for the isolated hardware helper. Requires the signed PawnIO driver. The dashboard remains unprivileged.",11,"#8ea3c0"));}
        var local=new TextBox{Text=service.Settings.OllamaUrl,PlaceholderText="http://127.0.0.1:11434"};stack.Children.Add(Text("Local Ollama server",13,null,true));stack.Children.Add(local);
        var remote=new TextBox{Text=string.Join('\n',service.Settings.RemoteEndpoints.Select(e=>e.Name+" | "+e.Url)),AcceptsReturn=true,MinHeight=74,PlaceholderText="ai-node-01 | http://server:11434"};stack.Children.Add(Text("Remote Ollama / future k3s endpoints · one Name | URL per line",13,null,true));stack.Children.Add(remote);
        stack.Children.Add(Text("Remote hardware and Kubernetes pod metrics require a future cluster collector. These endpoints currently discover remote Ollama models.",11,"#8ea3c0"));
        var agents=new CheckBox{Content="Read local Codex / Claude Code usage metadata",IsChecked=service.Settings.ReadAgentUsage};stack.Children.Add(agents);
        var start=new CheckBox{Content="Launch AI Pulse at sign-in",IsChecked=service.Settings.StartAtLogin};stack.Children.Add(start);
        var temp=new NumericUpDown{Value=(decimal)service.Settings.TemperatureWarning,Minimum=50,Maximum=120,Increment=1};stack.Children.Add(Text("Sustained temperature warning (°C) · applies after 30 seconds",13));stack.Children.Add(temp);
        var save=Btn("Save settings");save.Click+=(_,_)=>
        {
            try{Ollama.Validate(local.Text??"");var endpoints=new List<Endpoint>();foreach(var line in (remote.Text??"").Split('\n',StringSplitOptions.RemoveEmptyEntries)){var parts=line.Split('|',2);if(parts.Length!=2||string.IsNullOrWhiteSpace(parts[0]))throw new Exception("Use Name | URL for each remote endpoint.");var name=parts[0].Trim();if(name=="This PC"||endpoints.Any(e=>e.Name==name))throw new Exception("Host names must be unique.");Ollama.Validate(parts[1].Trim());endpoints.Add(new(name,parts[1].Trim()));}
            service.Settings.OllamaUrl=local.Text!.Trim();service.Settings.RemoteEndpoints=endpoints;service.Settings.ReadAgentUsage=agents.IsChecked==true;service.Settings.TemperatureWarning=(double)(temp.Value??90);service.Settings.StartAtLogin=start.IsChecked==true;Startup.Configure(service.Settings.StartAtLogin);Paths.Save(service.Settings);notice="Settings saved.";}catch(Exception e){notice=e.Message;}RenderBody();
        };stack.Children.Add(save);
        stack.Children.Add(Message("Ollama observation gateway",service.Gateway.Status+"\nFor exact request tokens, point an Ollama-compatible client to http://127.0.0.1:"+service.Settings.GatewayPort+". Calls directly to Ollama are not counted."));
        stack.Children.Add(Text("History & backup",17,null,true));stack.Children.Add(Text("Data folder: "+Paths.Root+"\nLast verified backup: "+(service.Settings.LastBackup??"Pending")+"\nNightly snapshots: 7 daily + 13 weekly. Local copies do not protect against drive failure.",12,"#a1b2c9"));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};var backup=Btn("Back up now");backup.Click+=async(_,_)=>{backup.IsEnabled=false;notice=await service.BackupNow() is string f?"Verified backup: "+Path.GetFileName(f):"Backup failed. Check available space.";RenderBody();};actions.Children.Add(backup);var folder=Btn("Open data folder");folder.Click+=(_,_)=>OpenFolder(Paths.Root);actions.Children.Add(folder);stack.Children.Add(actions);
        var restore=Btn("Restore a verified backup…");restore.Click+=async(_,_)=>
        {
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="Choose an AI Pulse backup",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("SQLite backup"){Patterns=["*.db"]}]});
            if(files.Count==0)return;string? file=files[0].TryGetLocalPath();if(file==null)return;
            try
            {
                Storage.VerifyBackup(file);
                var dialog=new Window{Title="Restore AI Pulse history",Width=460,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Canvas};
                var contents=new StackPanel{Margin=new Thickness(22),Spacing=18};contents.Children.Add(Text("Restore this snapshot?",18,null,true));contents.Children.Add(Text("AI Pulse will restart. Your current database will be preserved as a rollback copy.",13));var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};var yes=Btn("Restore and restart");yes.Click+=(_,_)=>dialog.Close(true);var no=Btn("Cancel");no.Click+=(_,_)=>dialog.Close(false);buttons.Children.Add(yes);buttons.Children.Add(no);contents.Children.Add(buttons);dialog.Content=contents;
                if(await dialog.ShowDialog<bool>(this))
                {
                    var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,ArgumentList={"--restore",file,"--data-dir",Paths.Root,"--wait-for-exit",Environment.ProcessId.ToString(),"--launch-after"}};Process.Start(info);
                    if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}
                }
            }catch(Exception e){notice="Restore was not started: "+e.Message;RenderBody();}
        };stack.Children.Add(restore);
        var quit=Btn("Quit AI Pulse · Ctrl+Q");quit.Click+=(_,_)=>Quit();stack.Children.Add(quit);
        if(notice!="")stack.Children.Add(Message("Status",notice));stack.Children.Add(Text("AI Pulse 0.1.0 · local-first · Avalonia / SQLite / LibreHardwareMonitor\nHardware coverage depends on your device and operating system.",11,"#7186a2"));return stack;
    }
    public void Capture()
    {
        try{var file=Path.Combine(Paths.Root,service.Settings.Compact?"compact-live.png":"dashboard-live.png");var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96));bitmap.Render(this);bitmap.Save(file);}catch(Exception e){Paths.Log("capture",e);}
    }
    static void OpenFolder(string folder){try{Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows()?"explorer.exe":"xdg-open"){ArgumentList={folder},UseShellExecute=true});}catch{}}
    static TextBlock Text(string text,double size=13,string? color=null,bool bold=false)=>new(){Text=text,FontSize=size,Foreground=Brush.Parse(color??"#e1e9f7"),FontWeight=bold?FontWeight.SemiBold:FontWeight.Normal,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    static Button Btn(string label)=>new(){Content=label,Cursor=new Cursor(StandardCursorType.Hand)};
    static Control Heading(string title,string sub){var s=new StackPanel{Spacing=7};s.Children.Add(Text(title,27,"#f0f4ff",true));s.Children.Add(Text(sub,12,"#8ba0bd"));return s;}
    static Border Card(Control child)=>new(){Child=child,Padding=new Thickness(18),Background=Panel,CornerRadius=new CornerRadius(10),BorderBrush=Line,BorderThickness=new Thickness(1)};
    static Border Metric(string label,string value,string caption){var s=new StackPanel{Spacing=10};s.Children.Add(Text(label,10,"#a1b4ce"));s.Children.Add(Text(value,29,"#e9f0ff",true));s.Children.Add(Text(caption,10,"#7f93af"));return Card(s);}
    static Control Pair(string label,string value){var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};grid.Children.Add(Text(label,12,"#9eb1ca"));var v=Text(value,16,"#72d4e8",true);Grid.SetColumn(v,1);grid.Children.Add(v);return grid;}
    static Control Message(string title,string detail){var s=new StackPanel{Spacing=6};s.Children.Add(Text(title,13,"#bba3ff",true));s.Children.Add(Text(detail,12,"#9eb1ca"));return Card(s);}
    static Control Table(string[] headers,IEnumerable<string[]> data,string columns)
    {
        var stack=new StackPanel();int index=0;
        void Row(string[] values,bool header){var grid=new Grid{ColumnDefinitions=new ColumnDefinitions(columns),Margin=new Thickness(12,header?9:12)};for(int i=0;i<values.Length;i++){var text=Text(values[i],header?10:12,header?"#8298b7":"#dbe5f4",header);text.Margin=new Thickness(0,0,10,0);Grid.SetColumn(text,i);grid.Children.Add(text);}stack.Children.Add(new Border{Child=grid,Background=header?Brush.Parse("#182438"):index++%2==0?Panel:Brush.Parse("#101927"),BorderBrush=Line,BorderThickness=new Thickness(0,0,0,1)});}
        Row(headers,true);foreach(var row in data)Row(row,false);return new Border{Child=stack,CornerRadius=new CornerRadius(7),ClipToBounds=true};
    }
}
