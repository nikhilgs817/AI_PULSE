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
            service.Settings.Compact=true;service.Settings.CompactVertical=false;modelsExpanded=false;ApplyWindowLayout();RenderBody();await Shot("compact-horizontal");
            Check(Width==560&&Height==132&&header.Bounds.Height==34,"Horizontal compact layout contains only the small header, five readings and model toggle");
            await Click("Toggle loaded models");Check(modelsExpanded&&Height>132,"Loaded-model dropdown opens without expanding the dashboard");await Shot("compact-models");
            int expectedIndex=Array.IndexOf(Program.Arguments,"--expect-models");
            if(expectedIndex>=0)
            {
                int expected=int.Parse(Program.Arguments[expectedIndex+1]);Check(service.Ollama.Models.Count==expected,"All fixture models were discovered through Ollama HTTP polling");
                foreach(var model in service.Ollama.Models)Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==model.Name),"Loaded-model row exists: "+model.Name);
                var scroll=this.GetVisualDescendants().OfType<ScrollViewer>().Single();Check(scroll.Extent.Height>scroll.Viewport.Height&&Height<=432,"Many models scroll inside a bounded compact dropdown");
                scroll.Offset=new Vector(0,scroll.Extent.Height);await Task.Delay(150);Refresh();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset.Y>0,"Loaded-model scroll position survives live refresh");
            }
            await Click("Toggle loaded models");Check(!modelsExpanded&&Height==132,"Closing loaded-model dropdown restores compact height");
            await Click("Switch compact orientation");Check(service.Settings.CompactVertical&&Width==260&&Height==300,"Vertical compact layout changes width and height");await Shot("compact-vertical");
            await Click("Expand dashboard");Check(!service.Settings.Compact&&header.Bounds.Height==64,"Expanded dashboard restores full-size branding");
            var pressedHeader=header.Content;ButtonNamed("Maximize window").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(pressedHeader,header.Content),"Window-state changes keep the active header attached until input completes");
            await Task.Delay(200);Check(WindowState==WindowState.Maximized&&ButtonNamed("Restore window")!=null,"Expanded window maximizes and exposes Restore");
            await Click("Restore window");Check(WindowState==WindowState.Normal&&ButtonNamed("Maximize window")!=null,"Restore returns to a normal resizable window");
            await Click("Maximize window");await Click("Show compact dashboard");Check(service.Settings.Compact&&WindowState==WindowState.Normal&&!CanMaximize,"Compact mode leaves maximization and remains a small widget");
            await Click("Expand dashboard");
            await Click("Overview");await Click("Overview");Check(scroller!=null&&page=="Overview","Clicking the current navigation tab repeatedly preserves its content");
            Check(this.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text=="GPU TEMPERATURE").FontSize==15,"Overview metric headings match the other card headings");
            await Shot("overview-expanded");
            await Click("Collapse menu");Check(service.Settings.SidebarCollapsed&&ButtonNamed("Expand menu")!=null,"Sidebar collapses to named icon buttons");await Shot("sidebar-collapsed");
            await Click("Hardware");var search=this.GetVisualDescendants().OfType<TextBox>().Single();search.Focus();search.Text="not-a-real-sensor-xyz";await Task.Delay(150);Refresh();await Task.Delay(150);
            Check(ReferenceEquals(search,this.GetVisualDescendants().OfType<TextBox>().Single())&&search.IsFocused&&search.Text=="not-a-real-sensor-xyz","Search text and keyboard focus survive live refresh");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="No hardware matches your search."),"Unmatched hardware search has an explicit empty state");
            search.Text="CPU";await Click("Hardware");Check(!this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Sensor access"||t.Text=="Live process activity"),"Hardware shows specifications without sensor readings or process activity");await Shot("hardware-search");
            await Click("Sensors");Check(this.GetVisualDescendants().OfType<TextBox>().Single().Text=="","Sensors has an independent search field");
            await Click("Sensors");Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Sensor access")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Live process activity"),"Sensors owns live readings, permission status and process activity");
            this.GetVisualDescendants().OfType<TextBox>().Single().Text="no-sensor-xyz";await Task.Delay(150);Refresh();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="No sensors match your search."),"Sensor search has an explicit empty state");
            this.GetVisualDescendants().OfType<TextBox>().Single().Text="CPU";await Shot("sensors-search");
            await Click("Hardware");Check(this.GetVisualDescendants().OfType<TextBox>().Single().Text=="CPU","Search survives tab changes");
            await Click("Agents");Check(AutomationProperties.GetName(this.GetVisualDescendants().OfType<TextBox>().Single())=="Search Agents","Agents exposes searchable retained session metadata");
            await Click("Expand menu");Check(!service.Settings.SidebarCollapsed,"Sidebar expands back to icon and label navigation");
            service.Store.PutUsage(new("fixture-tokens-ollama","Ollama","fixture-local-model","",J.Now,1200,300,200,0,0,null,null,"Completed"));
            service.Store.PutUsage(new("fixture-tokens-codex","Codex","fixture-code-model","fixture-session",J.Now,900,200,600,0,80,null,null,"Reported"));
            service.Store.PutUsage(new("fixture-tokens-claude","Claude Code","fixture-claude-model","fixture-session-2",J.Now,700,150,300,100,25,null,null,"Reported"));
            await Click("Token");await Click("Token");Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="On-prem models")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Tools & other usage"),"Token separates on-prem models from tools and other usage");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1,500")&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1,950"),"Token displays exact independent totals for both sections");await Shot("token-split");
            tokenDays=7;RenderBody();await Task.Delay(150);Check(tokenDays==7&&this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Cache write¹"),"Token supports period selection and detailed cache/reasoning columns");
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
