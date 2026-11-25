using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AiPulse;

public sealed partial class MainWindow
{
    // Invoked only by the explicit --ui-test command in an isolated test data directory.
    async Task RunUiChecks()
    {
        var results=new List<string>();timer.Stop();
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);results.Add("PASS: "+name);}
        Button ButtonNamed(string name)=>this.GetVisualDescendants().OfType<Button>().First(b=>AutomationProperties.GetName(b)==name);
        async Task Click(string name){ButtonNamed(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(200);for(int i=0;i<20&&localService.Settings.Compact&&Math.Abs(Height-CompactHeight())>0.1;i++)await Task.Delay(50);}
        async Task Shot(string name){await Task.Delay(250);using var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96));bitmap.Render(this);bitmap.Save(Path.Combine(Paths.Root,name+".png"));}
        bool CompactQuotasFit()
        {
            var rings = this.GetVisualDescendants().OfType<QuotaRing>().ToArray();
            var origins = rings.Select(r => r.TranslatePoint(default, this)).ToArray();
            return rings.Length == 4 && rings.All(r => r.Bounds.Width == 36 && r.Bounds.Height == 36) &&
                origins.All(p => p is { } origin && origin.Y == origins[0]?.Y && origin.X >= 0 && origin.X + 36 <= Width) &&
                origins.Zip(origins.Skip(1)).All(pair => pair.Second!.Value.X >= pair.First!.Value.X + 36);
        }
        try
        {
            int expectedIndex=Array.IndexOf(Program.Arguments,"--expect-models");
            var readyDeadline=DateTime.UtcNow.AddSeconds(30);
            while(DateTime.UtcNow<readyDeadline&&(service.Hardware?.Inventory.Count is null or 0||expectedIndex>=0&&service.Ollama.Models.Count!=int.Parse(Program.Arguments[expectedIndex+1])))await Task.Delay(250);
            service.Settings.Compact=true;service.Settings.CompactVertical=false;modelsExpanded=false;ApplyWindowLayout();RenderBody();await Shot("compact-horizontal");
            Check(Width==560&&Height==224&&header.Bounds.Height==34,"Horizontal compact layout fits the workload rows and quota row within a bounded footprint");
            Check(CompactQuotasFit() && !this.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "LAST DECODE"),
                "Horizontal compact layout keeps four quota rings in one row without Last Decode");
            Check(new[]{"GPU LOAD","GPU LOCAL GiB","RAM FREE"}.All(label=>this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==label))&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.StartsWith("HOTTEST")==true||t.Text=="GPU VR SOC"),"Compact shows resource headroom and temperature");
            Check(Background==Avalonia.Media.Brushes.Transparent&&TransparencyLevelHint.Contains(WindowTransparencyLevel.AcrylicBlur)&&WindowDecorations==WindowDecorations.None,"Compact mode requests native desktop blur with a transparent window background");
            Check(new[]{glassTint,glassSheen,glassEdge,glassInnerEdge}.All(layer=>layer.IsVisible&&!layer.IsHitTestVisible)&&TransparencyBackgroundFallback==Canvas,"Glass tint, reflections and edges preserve input routing and provide a dark compositor fallback");
            results.Add("INFO: Native compact backdrop on this desktop: "+ActualTransparencyLevel);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="ACTIVE AGENTS")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="LOCAL TOKENS TODAY"),"Compact mode displays agent activity and calendar-day tokens");
            await Click("Toggle loaded models");Check(modelsExpanded&&Height>CompactBaseHeight,"Loaded-model dropdown opens without expanding the dashboard");await Shot("compact-models");
            if(expectedIndex>=0)
            {
                int expected=int.Parse(Program.Arguments[expectedIndex+1]);Check(service.Ollama.Models.Count==expected,"All fixture models were discovered through Ollama HTTP polling");
                foreach(var model in service.Ollama.Models)Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==model.Name),"Loaded-model row exists: "+model.Name);
                var scroll=this.GetVisualDescendants().OfType<ScrollViewer>().Single();Check(scroll.Extent.Height>scroll.Viewport.Height&&Height<=CompactBaseHeight+300,"Many models scroll inside a bounded compact dropdown");
                scroll.Offset=new Vector(0,scroll.Extent.Height);await Task.Delay(150);Refresh();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset.Y>0,"Loaded-model scroll position survives live refresh");
            }
            await Click("Toggle loaded models");Check(!modelsExpanded&&Height==CompactBaseHeight,"Closing loaded-model dropdown restores compact height");
            await Click("Switch compact orientation");Check(service.Settings.CompactVertical&&Width==286&&Height==333,"Vertical compact layout fits the workload rows and quota row");await Shot("compact-vertical");
            Check(CompactQuotasFit() && !this.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "LAST DECODE"),
                "Vertical compact layout keeps four quota rings in one row without Last Decode");
            await Click("Expand dashboard");Check(!service.Settings.Compact&&header.Bounds.Height==52,"Expanded dashboard uses a shorter header");
            Check(Background==Canvas&&TransparencyLevelHint.SequenceEqual(new[]{WindowTransparencyLevel.None})&&!glassTint.IsVisible&&!glassEdge.IsVisible&&WindowDecorations==WindowDecorations.BorderOnly,"Expanding restores the opaque dashboard and normal window border");
            var pressedHeader=header.Content;ButtonNamed("Maximize window").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(pressedHeader,header.Content),"Window-state changes keep the active header attached until input completes");
            await Task.Delay(200);Check(WindowState==WindowState.Maximized&&ButtonNamed("Restore window")!=null,"Expanded window maximizes and exposes Restore");
            await Click("Restore window");Check(WindowState==WindowState.Normal&&ButtonNamed("Maximize window")!=null,"Restore returns to a normal resizable window");
            await Click("Maximize window");await Click("Show compact dashboard");Check(service.Settings.Compact&&WindowState==WindowState.Normal&&!CanMaximize,"Compact mode leaves maximization and remains a small widget");
            await Click("Expand dashboard");
            await Click("Overview");await Click("Overview");Check(scroller!=null&&page=="Overview","Clicking the current navigation tab repeatedly preserves its content");
            Check(this.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text=="GPU TEMPERATURE").FontSize==15,"Overview metric headings match the other card headings");
            Check(new[]{"CPU UTILIZATION","ACTIVE AGENTS","ON-PREM TOKENS · TODAY","TOOL TOKENS · TODAY","Agent resource consumption","Monitoring health","Storage & memory pressure","Wi-Fi speed & traffic"}.All(label=>this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==label)),"Overview includes hardware, workloads, independent daily token totals, Wi-Fi and collection health");
            Check(!this.GetVisualDescendants().OfType<Button>().Any(b=>AutomationProperties.GetName(b)=="Toggle compact theme"),"The expanded header only offers the dashboard theme");
            await Click("Show compact dashboard");await Click("Toggle compact theme");
            Check(localService.Settings.CompactLightTheme&&LightTheme,"Compact theme remains available in compact mode");
            await Click("Expand dashboard");
            Check(!LightTheme,"Compact theme leaves the dashboard theme independent");
            ToggleSwitch ThemeSwitch()=>this.GetVisualDescendants().OfType<ToggleSwitch>().Single(t=>AutomationProperties.GetName(t)=="Toggle dashboard theme");
            ThemeSwitch().IsChecked=true;await Task.Delay(200);
            Check(LightTheme&&RequestedThemeVariant==Avalonia.Styling.ThemeVariant.Light,"Dashboard header switches the dashboard palette");
            await Click("Edit layout");
            Check(!this.GetVisualDescendants().OfType<Button>().Any(b=>AutomationProperties.GetName(b)=="Assistant")&&
                ButtonNamed("Ask Pulse")!=null,"Chat launcher replaces the Assistant navigation tab");
            Check(TryCreateWidget("Build a 2×2 widget for GPU temperature",out var added)&&added.Contains("Widget shelf")&&
                TryCreateWidget("Create a widget for an unmonitored fixture value",out var rejected)&&rejected.Contains("couldn't match")&&
                TryCreateWidget("Build a 45×2 widget for CPU usage",out var invalid)&&invalid.Contains("Choose a widget size"),
                "Chat creates only recognized telemetry widgets with bounded grid dimensions");
            var widgetPrefs=Paths.Load();
            var created=widgetPrefs.CustomMetricWidgets.Single();
            Check(created.MetricId=="gpu.temperature"&&widgetPrefs.OverviewTileSizes[created.Id]==new OverviewTileSize(2,2),
                "Custom widget and its requested size persist across settings reload");
            Check(Level(null)==null&&Level(50)=="#59dfae"&&Level(78)=="#e8b65f"&&Level(95)=="#ff7187",
                "Threshold colors distinguish normal, warning, high and unavailable");
            await Click("Ask Pulse");
            Check(chatWindow?.IsVisible==true&&chatScroll?.Content is StackPanel,"Ask Pulse opens an independent chat window");
            chatWindow!.Close();
            async Task AddTile(string name)
            {
                var button=ButtonNamed("Widget shelf");
                var flyout=(Flyout)button.Flyout!;
                var shelf=(StackPanel)((ScrollViewer)flyout.Content!).Content!;
                flyout.ShowAt(button);
                await Task.Delay(100);
                shelf.Children.OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Add "+name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                flyout.Hide();
                await Task.Delay(100);
            }
            await AddTile("Active alerts");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Active alerts"),"Widget shelf adds optional panels");
            await Click("Remove CPU UTILIZATION");
            Check(!this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="Drag CPU UTILIZATION"),"Removing a tile hides it from the grid");
            await Click("Undo");
            Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="Drag CPU UTILIZATION"),"Undo returns a removed tile to its prior slot");
            await Click("Remove CPU UTILIZATION");
            await AddTile("CPU UTILIZATION");
            await Click("Move CPU UTILIZATION later");
            var savedLayout=Paths.Load();
            Check(savedLayout.OverviewTileOrder.IndexOf("CPU UTILIZATION")>savedLayout.OverviewTileOrder.IndexOf("GPU UTILIZATION")&&
                !savedLayout.HiddenOverviewTiles.Contains("CPU UTILIZATION")&&savedLayout.CompactLightTheme&&savedLayout.DashboardLightTheme,
                "Reordering and both theme preferences survive reloading settings");
            await Shot("overview-widget-grid-light");
            await Click("Reset layout");await Click("Done editing");
            ThemeSwitch().IsChecked=false;await Task.Delay(200);
            await Click("Show compact dashboard");await Click("Toggle compact theme");await Click("Expand dashboard");
            Check(this.GetVisualDescendants().OfType<TrendChart>().Count()>=3&&this.GetVisualDescendants().OfType<TrendChart>().All(c=>c.WindowEnd-c.WindowStart==900),"Overview trend panels share an explicit 15-minute dated time window");
            await Click("Overview range: 1 hour");Check(this.GetVisualDescendants().OfType<TrendChart>().All(c=>c.WindowEnd-c.WindowStart==3600),"Overview time-range selection updates every trend together");
            await Click("Overview: open Agents");Check(page=="Agents","Overview resource drill-down opens the Agents page");await Click("Overview");
            Width=1280;Height=820;await Task.Delay(250);Refresh();await Task.Delay(150);
            await Shot("overview-expanded");
            long wifiNow=J.Now;for(int i=0;i<6;i++)service.Store.Sample(wifiNow-360+i*60,[new("nic.fixture-wifi.rx","Download","Fixture Wi-Fi","MiB/s",i+1,"UI fixture"),new("nic.fixture-wifi.tx","Upload","Fixture Wi-Fi","MiB/s",(i+1)/2.0,"UI fixture")]);
            var wifiFixture=new List<NetworkSample>{new("nic.fixture-wifi","Fixture Wi-Fi",true,true,600,1,2)};
            scroller!.Content=WifiPanel(wifiFixture,wifiNow-900,wifiNow,true);await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="8.39 Mbps")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>(t.Text??"").Contains("link 600 Mbps"))&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="16.78 Mbps"),"Wi-Fi shows actual upload/download rates separately from the negotiated link speed");
            Check(this.GetVisualDescendants().OfType<TrendChart>().Single().Series.Count==2&&this.GetVisualDescendants().OfType<TrendChart>().Single().Series.All(s=>s.Points.Count==6),"Wi-Fi graph contains separately identified download/upload histories");await Shot("wifi-speed-fixture");
            scroller.Content=WifiPanel(wifiFixture,wifiNow-900,wifiNow,false);await Task.Delay(150);Check(!this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="8.39 Mbps"||t.Text=="16.78 Mbps"),"Stale Wi-Fi readings are unavailable rather than presented as current speed");
            Refresh();await Task.Delay(150);
            await Click("Collapse menu");Check(service.Settings.SidebarCollapsed&&ButtonNamed("Expand menu")!=null,"Sidebar collapses to named icon buttons");await Shot("sidebar-collapsed");
            await Click("Hardware");var search=this.GetVisualDescendants().OfType<TextBox>().Single();search.Focus();search.Text="not-a-real-sensor-xyz";await Task.Delay(150);Refresh();await Task.Delay(150);
            Check(ReferenceEquals(search,this.GetVisualDescendants().OfType<TextBox>().Single())&&search.IsFocused&&search.Text=="not-a-real-sensor-xyz","Search text and keyboard focus survive live refresh");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="No hardware matches your search."),"Unmatched hardware search has an explicit empty state");
            search.Text="CPU";await Click("Hardware");Check(!this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Sensor access"||t.Text=="Live process activity")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="CPU UTILIZATION"),"Hardware shows live capacity and utilization while deep-sensor controls stay in Sensors");await Shot("hardware-search");
            search.Text="";Refresh();await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="SYSTEM MEMORY")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="GPU LOCAL MEMORY")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Total capacity"),"Hardware exposes memory capacity, GPU allocation and disk used/available columns");
            Check(service.Readings.Where(r=>r.Id.StartsWith("nic.")&&r.Id.EndsWith(".speed")&&r.Value.HasValue).All(r=>Format(r.Id).EndsWith("Mbps"))&&service.Readings.Where(r=>r.Id.StartsWith("nic.")&&r.Id.EndsWith(".rx")&&r.Value.HasValue).All(r=>Format(r.Id).EndsWith("MiB/s")),"Network readings retain link-speed and transfer-rate units");await Shot("hardware-live");
            search.Text="CPU";
            await Click("Sensors");Check(this.GetVisualDescendants().OfType<TextBox>().Single().Text=="","Sensors has an independent search field");
            await Click("Sensors");Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Sensor access")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Live process activity"),"Sensors owns live readings, permission status and process activity");
            this.GetVisualDescendants().OfType<TextBox>().Single().Text="no-sensor-xyz";await Task.Delay(150);Refresh();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="No sensors match your search."),"Sensor search has an explicit empty state");
            this.GetVisualDescendants().OfType<TextBox>().Single().Text="CPU";await Shot("sensors-search");
            await Click("Hardware");Check(this.GetVisualDescendants().OfType<TextBox>().Single().Text=="CPU","Search survives tab changes");
            await Click("Agents");Check(this.GetVisualDescendants().OfType<TextBox>().Count(t=>AutomationProperties.GetName(t)=="Search Agents")==1,"Agents exposes searchable retained session metadata");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Live app resource consumption")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Working set (sum)"),"Agents displays live app CPU, working-set memory and process counts separately from sessions");
            var resourceFixture=AgentResources.Group([new(801,0,"codex",1,2,100),new(802,801,"fixture-worker",2,3,50)]);
            expandedAgentApps.Add("Codex");scroller!.Content=AgentResourcePanel(resourceFixture,DateTime.UtcNow,0);await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="5.0%")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="150 MiB")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="fixture-worker"),"Agent app totals and expandable child-process detail show the measured values");await Shot("agents-resources-fixture");
            scroller.Content=AgentResourcePanel(resourceFixture,DateTime.UtcNow.AddSeconds(-20),0);await Task.Delay(150);
            Check(!this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="5.0%"||t.Text=="150 MiB"),"Stale process samples do not masquerade as current resource consumption");
            expandedAgentApps.Clear();Refresh();await Task.Delay(150);
            await Click("Expand menu");Check(!service.Settings.SidebarCollapsed,"Sidebar expands back to icon and label navigation");
            service.Store.PutUsage(new("fixture-tokens-old","Codex","fixture-older-model","",J.Now-3*86400,7,3,0,0,0,null,null,"Reported"));
            await Click("Token");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Text=="No observations in this window")==2&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Older records are available")==true),"Empty token windows explain missing observations and retained history instead of reporting measured zero");await Shot("token-empty-window");
            await Click("Show older recorded usage");Check(tokenDays==service.Settings.RetentionDays&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="fixture-older-model"||t.Text?.Contains("fixture-older-model")==true),"Older token observations can be opened directly from an empty recent window");
            await Click("Token range: Today");Check(tokenDays==0&&TokenWindowStart(J.Now)==J.DayStart(DateTimeOffset.UtcNow,TimeZoneInfo.Local)&&this.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Text=="No observations in this window")==2,"Today uses local midnight and excludes older usage");
            await Click("Token range: 1");
            service.Store.PutUsage(new("fixture-tokens-ollama","Ollama","fixture-local-model","",J.Now,1200,300,200,0,0,null,null,"Completed"));
            service.Store.PutUsage(new("fixture-tokens-codex","Codex","fixture-code-model","fixture-session",J.Now,900,200,600,0,80,null,null,"Reported"));
            service.Store.PutUsage(new("fixture-tokens-claude","Claude Code","fixture-claude-model","fixture-session-2",J.Now,700,150,300,100,25,null,null,"Reported"));
            long counterNow=J.Now;
            service.Store.ObserveCounter("fixture-vllm","vLLM counters","fixture-pod","fixture-vllm-model","output",counterNow,counterNow,48,false);
            service.Store.RecordAgentSpawn("Codex","fixture-agent",counterNow);
            RenderBody();
            await Click("Token");await Click("Token");Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="On-prem models")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Tools & other usage"),"Token separates on-prem models from tools and other usage");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1,500")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1,950"),"Token displays exact independent totals for both sections");await Shot("token-split");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="vLLM server tokens")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="48"),"Token exposes vLLM server counters separately from observed requests");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.StartsWith("Codex · last recorded")==true)&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Import disabled")==true),"Token page exposes last-recorded times and collection status separately from saved totals");
            var quotaNow = DateTimeOffset.UtcNow;
            nextQuotaRead = quotaNow.AddMinutes(5);
            codexQuota = new(new(32, quotaNow.AddHours(1)), new(63, quotaNow.AddDays(2)), quotaNow);
            claudeQuota = new(new(84, quotaNow.AddHours(2)), new(50, quotaNow.AddDays(4)), quotaNow);
            localService.Settings.ReadAgentUsage = true;
            RenderBody();
            await Shot("token-subscription-quotas");
            var quotaRings = this.GetVisualDescendants().OfType<QuotaRing>().ToArray();
            Check(quotaRings.Length == 4 && quotaRings.All(r => r.TranslatePoint(new Point(0, 0), this)?.Y == quotaRings[0].TranslatePoint(new Point(0, 0), this)?.Y)
                && new[] { "68%", "37%", "16%", "50%" }.All(percent => this.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == percent)),
                "Token displays the four remaining percentages in one row");
            localService.Settings.ReadAgentUsage = false;
            tokenDays=7;RenderBody();await Task.Delay(150);Check(tokenDays==7&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Cache write¹"),"Token supports period selection and detailed cache/reasoning columns");
            await Click("Show compact dashboard");Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="LOCAL TOKENS TODAY: "+J.Count(1500)),"Compact daily token value updates from stored input/output observations");
            Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="ACTIVE AGENTS: —"),"Disabled agent import is shown as unavailable rather than zero active agents");
            Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="vLLM TOKENS TODAY: 48"),"Compact shows vLLM tokens in the same local-day window");
            Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="AGENT SPAWNS TODAY: 1"),"Compact shows distinct observed starts independently of recent session activity");
            await Shot("compact-activity");
            localService.Settings.ReadAgentUsage = true;
            RenderBody();
            await Shot("compact-subscription-quotas");
            Check(CompactQuotasFit() && new[] { "68%", "37%", "16%", "50%" }.All(percent => this.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == percent)),
                "Compact quota rings show the same remaining percentages as Token");
            localService.Settings.ReadAgentUsage = false;
            await Click("Expand dashboard");
            await Click("History");Check(!this.GetVisualDescendants().OfType<Button>().Any(b=>Equals(b.Content,"365 days")),"Default 90-day history does not offer unavailable year ranges");
            service.Settings.RetentionDays=365;service.Store.RetentionDays=365;RenderBody();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<Button>().Any(b=>Equals(b.Content,"365 days")),"One-year retention enables the 365-day history range");
            await Shot("history-year");
            await Click("Disk");Check(page=="Disk"&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Disk space"),"Disk page opens from the navigation");
            var diskFixture=Path.Combine(Paths.Root,"disk-fixture");Directory.CreateDirectory(Path.Combine(diskFixture,"cache","huggingface","hub"));Directory.CreateDirectory(Path.Combine(diskFixture,"web","node_modules"));
            File.WriteAllBytes(Path.Combine(diskFixture,"cache","huggingface","hub","weights.bin"),new byte[3<<20]);File.WriteAllBytes(Path.Combine(diskFixture,"web","node_modules","pkg.js"),new byte[1<<20]);
            StartDiskScan(diskFixture);for(int i=0;i<100&&diskScan!=null;i++)await Task.Delay(100);await Task.Delay(150);
            Check(diskRoot?.Size==(4<<20)&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.StartsWith("Hugging Face cache")==true)&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="cache"),"Disk scan sizes folders and labels AI caches");
            Check(this.GetVisualDescendants().OfType<Button>().Count(b=>Equals(b.Content,"Delete…"))>=4,"Every scanned folder row offers delete");
            await Shot("disk");
            await Click("Drive");Check(page=="Drive"&&this.GetVisualDescendants().OfType<TextBox>().Any(t=>t.PlaceholderText?.StartsWith("Client ID")==true),"Drive page asks for an OAuth client before anything else");
            await Shot("drive-setup");
            localService.Settings.DriveClientId="test.apps.googleusercontent.com";driveAccount=null;File.WriteAllText(Path.Combine(Paths.Root,"drive-token.bin"),"x");
            driveItems=[new("d1","Datasets",true,0,["root"],default,true),new("f1","train.parquet",false,6<<20,["d1"],new DateTime(2026,5,1),true),new("f2","photo.jpg",false,1<<20,["root"],default,true),new("f3","shared.bin",false,2<<20,["someone-else"],default,false)];
            driveRoot=driveCurrent=DriveTree.Build(driveItems,"root");driveQuota=new(15L<<30,9L<<20,9L<<20,0);RenderBody();await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Datasets")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==DriveTree.Elsewhere)&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="train.parquet"),"Drive tree shows folders, the outside-My-Drive bucket and largest files");
            Check(this.GetVisualDescendants().OfType<Button>().Count(b=>Equals(b.Content,"Delete…"))==3,"Drive offers delete only where Drive allows trashing");
            await Shot("drive");
            File.Delete(Path.Combine(Paths.Root,"drive-token.bin"));localService.Settings.DriveClientId="";driveAccount=null;driveRoot=driveCurrent=null;driveItems=[];driveQuota=null;
            await Click("Settings");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="AI")&&
                this.GetVisualDescendants().OfType<Button>().Any(b=>Equals(b.Content,"Download selected model")),"Settings contains the optional AI setup");
            Check(this.GetVisualDescendants().OfType<NumericUpDown>().Any(c=>AutomationProperties.GetName(c)=="Backup storage budget in MiB"&&c.Value==512)&&this.GetVisualDescendants().OfType<NumericUpDown>().Any(c=>AutomationProperties.GetName(c)=="Daily backup restore points"&&c.Value==3)&&this.GetVisualDescendants().OfType<NumericUpDown>().Any(c=>AutomationProperties.GetName(c)=="Weekly backup restore points"&&c.Value==4)&&this.GetVisualDescendants().OfType<Button>().Any(b=>Equals(b.Content,"Optimize existing backups")),"Settings exposes independent compressed-backup retention and storage limits without reducing monitoring history");
            await Shot("settings");
            await RunFleetUiChecks(results);
            File.WriteAllLines(Path.Combine(Paths.Root,"ui-test-results.txt"),results);
            if(Environment.GetEnvironmentVariable("AIPULSE_RENDER_BENCH")=="1")await RunRenderBench();
        }
        catch(Exception e){results.Add("FAIL: "+e);results.Add($"Layout: compact={localService.Settings.Compact}, vertical={localService.Settings.CompactVertical}, modelsExpanded={modelsExpanded}, width={Width}, height={Height}, bounds={Bounds}, transparency={ActualTransparencyLevel}");await Shot("ui-failure");File.WriteAllLines(Path.Combine(Paths.Root,"ui-test-results.txt"),results);Environment.ExitCode=1;}
        finally{if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(Environment.ExitCode);}}
    }
}
