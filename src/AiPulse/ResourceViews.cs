using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiPulse;

public sealed partial class MainWindow
{
    readonly HashSet<string> expandedAgentApps=[];
    Control AgentResourcePanel(List<AgentResource> apps,DateTime sampled,int unreadable)
    {
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(Text("Live app resource consumption",16,null,true));
        bool stale=sampled==default||(DateTime.UtcNow-sampled).TotalSeconds>15;
        panel.Children.Add(Text(sampled==default?"Collecting processes…":$"Updated {sampled.ToLocalTime():HH:mm:ss} · refreshes about every 6 seconds"+(stale?" · sample is stale":""),11,"#8ba0bd"));
        var visible=apps.Where(a=>Matches(a.App)||a.Processes.Any(p=>Matches(p.Name,p.Id.ToString()))).ToList();
        panel.Children.Add(Table(["App / service","CPU (host %)","Working set (sum)","Processes"],visible.Select(a=>new[]{a.App,stale?"—":a.Cpu.HasValue?$"{a.Cpu:0.0}%":"Warming up",stale?"—":$"{a.MemoryMiB:N0} MiB",a.Processes.Count.ToString()}),"1.4*,*,1.3*,*"));
        if(visible.Count==0)panel.Children.Add(Text(apps.Count>0?"No running apps match your search.":"No readable Codex, Claude or Ollama processes detected on this computer.",13,"#8da1bd"));
        panel.Children.Add(Text("App totals include readable child processes and are shared across sessions. CPU is a share of the whole host; memory sums working sets and may include shared pages. Local app usage does not measure cloud server resources. Per-session CPU/RAM and per-process GPU usage are not exposed.",12,"#9eb1ca"));
        if(unreadable>0)panel.Children.Add(Text($"{unreadable} system processes could not be read; app totals may be incomplete if a protected process belongs to the app.",11,"#8ba0bd"));
        foreach(var app in visible)
        {
            var processes=app.Processes.Where(p=>Matches(app.App,p.Name,p.Id.ToString()));
            var detail=new Expander{Header=app.App+" · process details",IsExpanded=expandedAgentApps.Contains(app.App),HorizontalAlignment=HorizontalAlignment.Stretch,Content=Table(["Process","PID","CPU (host %)","Working set"],processes.Select(p=>new[]{p.Name,p.Id.ToString(),stale?"—":p.Cpu?.ToString("0.0")??"Warming up",stale?"—":$"{p.MemoryMiB:N0} MiB"}),"2*,*,*,*")};
            detail.PropertyChanged+=(_,e)=>{if(e.Property==Expander.IsExpandedProperty){if(detail.IsExpanded)expandedAgentApps.Add(app.App);else expandedAgentApps.Remove(app.App);}};
            panel.Children.Add(detail);
        }
        return Card(panel);
    }
}
