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

// Models, Agents, History and Alerts pages.
public sealed partial class MainWindow
{
    Control ModelsPage()
    {
        var stack=new StackPanel{Spacing=18};stack.Children.Add(Heading("Models","Installed is different from loaded. Generating is confirmed only for observed requests."));stack.Children.Add(ModelsList(false));stack.Children.Add(ModelInsights());
        var inventory=new StackPanel{Spacing=12};inventory.Children.Add(Text("Installed inventory",15,null,true));inventory.Children.Add(Table(["Model","Host","Quantization","Disk size"],service.Ollama.Installed.Select(m=>new[]{m.Name,ModelHost(m),m.Quantization,J.Size(m.MemoryGiB)}),"2*,*,*,*"));stack.Children.Add(Card(inventory));
        stack.Children.Add(Text("Recent observed requests",16,null,true));stack.Children.Add(Table(["Model","Input","Output","Decode tok/s","Duration","Outcome"],service.Store.UsageSince(J.Now-86400).Where(u=>u.Source=="Ollama").Take(100).Select(u=>new[]{u.Model,u.Status!="Completed"?"—":J.Count(u.Input),u.Status!="Completed"?"—":J.Count(u.Output),u.DecodeRate?.ToString("0.0")??"—",u.DurationMs.HasValue?$"{u.DurationMs/1000:0.0}s":"—",u.Status}),"2*,*,*,*,*,2*"));return stack;
    }
    Control ModelsList(bool inventory)
    {
        var stack=new StackPanel{Spacing=12};stack.Children.Add(Text("Loaded models · "+service.Ollama.Models.Count,16,null,true));
        foreach(var status in service.Ollama.Status)stack.Children.Add(Text(status.Key+"  ·  "+status.Value,11,status.Value=="Connected"?"#59dfae":"#e8b65f"));
        if(service.Ollama.Models.Count>0)
        {
            var largest=service.Store.LargestPrompt(J.Now-86400);
            string Context(ModelInfo m)=>m.Context<=0?"—":largest.TryGetValue(m.Name,out long peak)&&peak>0?$"{J.Count(m.Context)}\nlargest prompt {Math.Min(999,100.0*peak/m.Context):0}%":J.Count(m.Context);
            stack.Children.Add(Table(["Model / host","State","Model memory","GPU-reported","Context · 24 h peak use"],service.Ollama.Models.Select(m=>new[]{m.Name+"\n"+ModelHost(m),m.State,J.Size(m.MemoryGiB),J.Size(m.GpuGiB),Context(m)}),"2.2*,*,*,*,1.2*"));
        }
        else stack.Children.Add(Text("No models are loaded. Start an Ollama request to see residency here.",13,"#8da1bd"));
        if(inventory){stack.Children.Add(Text("Installed inventory",15,null,true));stack.Children.Add(Table(["Model","Host","Quantization","Disk size"],service.Ollama.Installed.Select(m=>new[]{m.Name,ModelHost(m),m.Quantization,J.Size(m.MemoryGiB)}),"2*,*,*,*"));}
        return Card(stack);
    }
    Control AgentsPage()
    {
        var stack=new StackPanel{Spacing=16};stack.Children.Add(Heading("Agents","Live app resources and retained session activity"));stack.Children.Add(AgentActivity());
        stack.Children.Add(AgentResourcePanel(service.AgentApps,service.ProcessSampleTime,service.UnreadableProcesses));
        long now=J.Now;
        var spawns=service.Store.AgentSpawns(J.DayStart(DateTimeOffset.FromUnixTimeSeconds(now),TimeZoneInfo.Local),now);
        stack.Children.Add(Metric("Agent spawns today",spawns.Count==0?"—":spawns.Sum(s=>s.Count).ToString("N0"),
            spawns.Count==0?"No agent starts reported today. Coverage requires supported agent metadata or a lifecycle exporter.":string.Join(" · ",spawns.Select(s=>$"{s.Source} {s.Count:N0}"))));
        stack.Children.Add(Text("Session activity & tokens",16,null,true));
        stack.Children.Add(Text(service.AgentStatus,12,"#8ba0bd"));
        stack.Children.Add(Message("Metadata only","Input/output/cache tokens are read from supported local session records. Prompts, replies and tool output are not stored. Quiet sessions are not assumed completed."));
        var sessions=service.Store.Sessions(service.Settings.RetentionDays,Query);stack.Children.Add(Table(["Source / session","Model","State","Input","Output","Last activity"],sessions.Select(s=>new[]{s.Source+"\n"+s.Id[..Math.Min(12,s.Id.Length)],s.Model,J.Now-s.LastSeen>300&&s.State is "Working" or "Recent activity"?"Quiet / unknown":s.State,J.Count(s.Input),J.Count(s.Output),DateTimeOffset.FromUnixTimeSeconds(s.LastSeen).ToLocalTime().ToString("MMM d HH:mm")}),"1.5*,1.7*,1.3*,*,*,1.3*"));
        if(sessions.Count==0)stack.Children.Add(Text("No sessions match. Search includes all retained sessions; up to 100 results are shown.",13,"#8da1bd"));return stack;
    }
    int historyDays=1;
    Control HistoryPage()
    {
        var stack=new StackPanel{Spacing=18};stack.Children.Add(Heading("History",$"7-day detailed readings · {service.Settings.RetentionDays}-day minute summaries and usage records"));var options=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(int days in new[]{1,7,30,90,180,365}.Where(d=>d<=service.Settings.RetentionDays).Append(service.Settings.RetentionDays).Distinct().Order()){var b=Btn(days+" day"+(days==1?"":"s"));if(days==historyDays)b.Background=Brush.Parse("#443477");b.Click+=(_,_)=>{historyDays=days;RenderBody();};options.Children.Add(b);}stack.Children.Add(options);
        historyDays=Math.Min(historyDays,service.Settings.RetentionDays);long historyEnd=J.Now,since=historyEnd-historyDays*86400L;var totals=service.Store.Totals(since);stack.Children.Add(Metric("OBSERVED TOKENS",J.Count(totals.Input+totals.Output),$"Input {J.Count(totals.Input)} · output {J.Count(totals.Output)} · cached input {J.Count(totals.Cached)} (subset)"));
        foreach(var metric in new[]{("gpu.temperature","GPU temperature","°C"),("cpu.load","CPU utilization","%"),("ram.used","System memory used","GiB")}){var points=service.Store.History(metric.Item1,since,historyEnd);var chartStack=new StackPanel{Spacing=8};chartStack.Children.Add(Text(metric.Item2,15,null,true));chartStack.Children.Add(new TrendChart{Height=180,Points=points,Unit=metric.Item3,WindowStart=since,WindowEnd=historyEnd,ExpectedIntervalSeconds=Storage.HistoryBucket(since,historyEnd),MaxValue=metric.Item3=="%"?100:null});chartStack.Children.Add(Text("Source: local collector · sample means; gaps indicate missing observations",10,"#7f93af"));stack.Children.Add(Card(chartStack));}
        var export=Btn("Export usage CSV");export.Click+=async(_,_)=>{export.IsEnabled=false;try{notice="Saved "+await Task.Run(service.Export);}catch(Exception e){Paths.Log("export",e);notice="Export failed. Check available space and folder access.";}RenderBody();};stack.Children.Add(export);if(notice!="")stack.Children.Add(Text(notice,11,"#59dfae"));return stack;
    }
    Control AlertsPage()
    {
        var stack=new StackPanel{Spacing=14};stack.Children.Add(Heading("Alerts","Sustained thresholds · no automatic model termination"));stack.Children.Add(HealthCard());var alerts=service.Store.Alerts();foreach(var a in alerts.Where(a=>a.Active))stack.Children.Add(Message(a.Title,a.Detail));if(!alerts.Any(a=>a.Active))stack.Children.Add(Message("No active alerts","Monitoring temperature, memory pressure, storage and backup health."));stack.Children.Add(Text("Recent checks",15,null,true));stack.Children.Add(Table(["Check","State","Changed"],alerts.Select(a=>new[]{a.Title,a.Active?"Attention":"Clear",DateTimeOffset.FromUnixTimeSeconds(a.Time).ToLocalTime().ToString("MMM d HH:mm")}),"2*,*,*"));return stack;
    }
}
