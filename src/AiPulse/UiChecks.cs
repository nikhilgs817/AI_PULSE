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
        async Task Click(string name){ButtonNamed(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(200);}
        async Task Shot(string name){await Task.Delay(250);using var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96));bitmap.Render(this);bitmap.Save(Path.Combine(Paths.Root,name+".png"));}
        try
        {
            int expectedIndex=Array.IndexOf(Program.Arguments,"--expect-models");
            var readyDeadline=DateTime.UtcNow.AddSeconds(30);
            while(DateTime.UtcNow<readyDeadline&&(service.Hardware?.Inventory.Count is null or 0||expectedIndex>=0&&service.Ollama.Models.Count!=int.Parse(Program.Arguments[expectedIndex+1])))await Task.Delay(250);
            service.Settings.Compact=true;service.Settings.CompactVertical=false;modelsExpanded=false;ApplyWindowLayout();RenderBody();await Shot("compact-horizontal");
            Check(Width==560&&Height==142&&header.Bounds.Height==34,"Horizontal compact layout includes five hardware readings, daily activity and model toggle");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="ACTIVE AGENTS")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="TOKENS TODAY"),"Compact mode displays agent activity and calendar-day tokens");
            await Click("Toggle loaded models");Check(modelsExpanded&&Height>142,"Loaded-model dropdown opens without expanding the dashboard");await Shot("compact-models");
            if(expectedIndex>=0)
            {
                int expected=int.Parse(Program.Arguments[expectedIndex+1]);Check(service.Ollama.Models.Count==expected,"All fixture models were discovered through Ollama HTTP polling");
                foreach(var model in service.Ollama.Models)Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==model.Name),"Loaded-model row exists: "+model.Name);
                var scroll=this.GetVisualDescendants().OfType<ScrollViewer>().Single();Check(scroll.Extent.Height>scroll.Viewport.Height&&Height<=442,"Many models scroll inside a bounded compact dropdown");
                scroll.Offset=new Vector(0,scroll.Extent.Height);await Task.Delay(150);Refresh();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset.Y>0,"Loaded-model scroll position survives live refresh");
            }
            await Click("Toggle loaded models");Check(!modelsExpanded&&Height==142,"Closing loaded-model dropdown restores compact height");
            await Click("Switch compact orientation");Check(service.Settings.CompactVertical&&Width==260&&Height==256,"Vertical compact layout fits seven readings in tight 28-pixel rows");await Shot("compact-vertical");
            await Click("Expand dashboard");Check(!service.Settings.Compact&&header.Bounds.Height==64,"Expanded dashboard restores full-size branding");
            var pressedHeader=header.Content;ButtonNamed("Maximize window").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(pressedHeader,header.Content),"Window-state changes keep the active header attached until input completes");
            await Task.Delay(200);Check(WindowState==WindowState.Maximized&&ButtonNamed("Restore window")!=null,"Expanded window maximizes and exposes Restore");
            await Click("Restore window");Check(WindowState==WindowState.Normal&&ButtonNamed("Maximize window")!=null,"Restore returns to a normal resizable window");
            await Click("Maximize window");await Click("Show compact dashboard");Check(service.Settings.Compact&&WindowState==WindowState.Normal&&!CanMaximize,"Compact mode leaves maximization and remains a small widget");
            await Click("Expand dashboard");
            await Click("Overview");await Click("Overview");Check(scroller!=null&&page=="Overview","Clicking the current navigation tab repeatedly preserves its content");
            Check(this.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text=="GPU TEMPERATURE").FontSize==15,"Overview metric headings match the other card headings");
            Check(new[]{"CPU UTILIZATION","ACTIVE AGENTS","ON-PREM TOKENS · TODAY","TOOL TOKENS · TODAY","Agent resource consumption","Monitoring health","Storage & memory pressure","Wi-Fi speed & traffic"}.All(label=>this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==label)),"Overview includes hardware, workloads, independent daily token totals, Wi-Fi and collection health");
            Check(this.GetVisualDescendants().OfType<TrendChart>().Count()>=3&&this.GetVisualDescendants().OfType<TrendChart>().All(c=>c.WindowEnd-c.WindowStart==900),"Overview trend panels share an explicit 15-minute dated time window");
            await Click("Overview range: 1 hour");Check(this.GetVisualDescendants().OfType<TrendChart>().All(c=>c.WindowEnd-c.WindowStart==3600),"Overview time-range selection updates every trend together");
            await Click("Overview: open Agents");Check(page=="Agents","Overview resource drill-down opens the Agents page");await Click("Overview");
            Width=1280;Height=820;await Task.Delay(250);Refresh();await Task.Delay(150);
            await Shot("overview-expanded");
            long wifiNow=J.Now;for(int i=0;i<6;i++)service.Store.Sample(wifiNow-360+i*60,[new("nic.fixture-wifi.rx","Download","Fixture Wi-Fi","MiB/s",i+1,"UI fixture"),new("nic.fixture-wifi.tx","Upload","Fixture Wi-Fi","MiB/s",(i+1)/2.0,"UI fixture")]);
            var wifiFixture=new List<NetworkSample>{new("nic.fixture-wifi","Fixture Wi-Fi",true,true,600,1,2)};
            scroller!.Content=WifiPanel(wifiFixture,wifiNow-900,wifiNow,true);await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="8.39 Mbps")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="16.78 Mbps")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text!.Contains("link 600 Mbps")),"Wi-Fi shows actual upload/download rates separately from the negotiated link speed");
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
            await Click("Agents");Check(AutomationProperties.GetName(this.GetVisualDescendants().OfType<TextBox>().Single())=="Search Agents","Agents exposes searchable retained session metadata");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Live app resource consumption")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Working set (sum)"),"Agents displays live app CPU, working-set memory and process counts separately from sessions");
            var resourceFixture=AgentResources.Group([new(801,0,"codex",1,2,100),new(802,801,"fixture-worker",2,3,50)]);
            expandedAgentApps.Add("Codex");scroller!.Content=AgentResourcePanel(resourceFixture,DateTime.UtcNow,0);await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="5.0%")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="150 MiB")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="fixture-worker"),"Agent app totals and expandable child-process detail show the measured values");await Shot("agents-resources-fixture");
            scroller.Content=AgentResourcePanel(resourceFixture,DateTime.UtcNow.AddSeconds(-20),0);await Task.Delay(150);
            Check(!this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="5.0%"||t.Text=="150 MiB"),"Stale process samples do not masquerade as current resource consumption");
            expandedAgentApps.Clear();Refresh();await Task.Delay(150);
            await Click("Expand menu");Check(!service.Settings.SidebarCollapsed,"Sidebar expands back to icon and label navigation");
            service.Store.PutUsage(new("fixture-tokens-ollama","Ollama","fixture-local-model","",J.Now,1200,300,200,0,0,null,null,"Completed"));
            service.Store.PutUsage(new("fixture-tokens-codex","Codex","fixture-code-model","fixture-session",J.Now,900,200,600,0,80,null,null,"Reported"));
            service.Store.PutUsage(new("fixture-tokens-claude","Claude Code","fixture-claude-model","fixture-session-2",J.Now,700,150,300,100,25,null,null,"Reported"));
            await Click("Token");await Click("Token");Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="On-prem models")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Tools & other usage"),"Token separates on-prem models from tools and other usage");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1,500")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1,950"),"Token displays exact independent totals for both sections");await Shot("token-split");
            tokenDays=7;RenderBody();await Task.Delay(150);Check(tokenDays==7&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Cache write¹"),"Token supports period selection and detailed cache/reasoning columns");
            await Click("Show compact dashboard");Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="TOKENS TODAY: "+J.Count(3450)),"Compact daily token value updates from stored input/output observations");
            Check(this.GetVisualDescendants().OfType<Control>().Any(c=>AutomationProperties.GetName(c)=="ACTIVE AGENTS: —"),"Disabled agent import is shown as unavailable rather than zero active agents");
            await Shot("compact-activity");await Click("Expand dashboard");
            await Click("History");Check(!this.GetVisualDescendants().OfType<Button>().Any(b=>Equals(b.Content,"365 days")),"Default 90-day history does not offer unavailable year ranges");
            service.Settings.RetentionDays=365;service.Store.RetentionDays=365;RenderBody();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<Button>().Any(b=>Equals(b.Content,"365 days")),"One-year retention enables the 365-day history range");
            await Shot("history-year");
            await Click("Settings");await Shot("settings");
            File.WriteAllLines(Path.Combine(Paths.Root,"ui-test-results.txt"),results);
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);File.WriteAllLines(Path.Combine(Paths.Root,"ui-test-results.txt"),results);Environment.ExitCode=1;}
        finally{if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(Environment.ExitCode);}}
    }
}
