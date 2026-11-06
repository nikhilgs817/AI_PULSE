using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiPulse;

public sealed partial class MainWindow
{
    int tokenDays=1;
    bool tokenExportBusy;
    string tokenNotice="";
    long TokenWindowStart(long now)=>tokenDays==0?J.DayStart(DateTimeOffset.FromUnixTimeSeconds(now),TimeZoneInfo.Local):now-tokenDays*86400L;
    static bool OnPremSource(string source)=>source is "Ollama" or "AIPULSE Assistant";
    Control TokenPage()
    {
        var stack=new StackPanel{Spacing=18};
        stack.Children.Add(Heading("Token","Observed token usage · on-prem models and tools shown separately"));
        tokenDays=Math.Min(tokenDays,service.Settings.RetentionDays);
        long now=J.Now,since=TokenWindowStart(now);
        var periods=new WrapPanel{Orientation=Orientation.Horizontal};
        foreach(int days in new[]{0,1,7,30,90,180,365}.Where(d=>d<=service.Settings.RetentionDays).Append(service.Settings.RetentionDays).Distinct().Order())
        {
            var b=Btn(days==0?"Today":days==1?"Last 24 hours":days==service.Settings.RetentionDays?$"All retained · {days} days":$"{days} days");b.Margin=new Thickness(0,0,8,8);if(days==tokenDays)b.Background=Brush.Parse("#443477");
            Avalonia.Automation.AutomationProperties.SetName(b,"Token range: "+(days==0?"Today":days==service.Settings.RetentionDays?"All retained":days.ToString()));
            b.Click+=(_,_)=>{tokenDays=days;RenderBody();};periods.Children.Add(b);
        }
        stack.Children.Add(periods);
        stack.Children.Add(Text($"{DateTimeOffset.FromUnixTimeSeconds(since).ToLocalTime():MMM d, yyyy HH:mm} → {DateTimeOffset.FromUnixTimeSeconds(now).ToLocalTime():MMM d, yyyy HH:mm zzz} · {service.HostName}",12,"#9eb1ca"));
        var rows=service.Store.TokenBreakdown(since,now);
        var coverage=service.Store.TokenCoverage(now);
        var onPrem=rows.Where(r=>r.OnPrem).ToList();var tools=rows.Where(r=>!r.OnPrem).ToList();
        var summary=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*")};
        Control Summary(string title,List<TokenBreakdown> records)=>Metric(title,records.Count==0?"—":records.Sum(r=>r.Total).ToString("N0"),records.Count==0?"No observations in this window":$"Input {records.Sum(r=>r.Input):N0} · output {records.Sum(r=>r.Output):N0}");
        var local=Summary("On-prem models",onPrem);summary.Children.Add(local);
        var other=Summary("Tools & other usage",tools);other.Margin=new Thickness(12,0,0,0);Grid.SetColumn(other,1);summary.Children.Add(other);stack.Children.Add(summary);
        var collection=new StackPanel{Spacing=8};collection.Children.Add(Text("Collection & recorded history",16,null,true));
        collection.Children.Add(Text("Local tools: "+(service.Settings.ReadAgentUsage?service.AgentStatus:"Import disabled. Enable local usage metadata in Settings."),12,"#9eb1ca"));
        collection.Children.Add(Text("Ollama gateway: "+service.GatewayStatus,12,"#9eb1ca"));
        if(service.IsRemote&&!service.RemoteLive)collection.Children.Add(Text("Host is offline or stale. Showing the history received so far.",12,"#e8b65f"));
        foreach(var source in coverage)collection.Children.Add(Text($"{source.Source} · last recorded {DateTimeOffset.FromUnixTimeSeconds(source.LastSeen).ToLocalTime():MMM d, yyyy HH:mm:ss zzz} · {source.Tokens:N0} tokens in retained history",12,"#9eb1ca"));
        if(coverage.Count==0)collection.Children.Add(Text("No retained usage records yet. Running an app or loading a model does not provide token counts.",12,"#9eb1ca"));
        var older=coverage.Where(c=>c.LastSeen<since).ToList();
        if(older.Count>0)
        {
            var show=Btn("Show older recorded usage");Avalonia.Automation.AutomationProperties.SetName(show,"Show older recorded usage");show.Click+=(_,_)=>{tokenDays=service.Settings.RetentionDays;RenderBody();};collection.Children.Add(show);
        }
        stack.Children.Add(Card(collection));
        string Empty(bool localSource)=>coverage.Any(c=>OnPremSource(c.Source)==localSource)?"No usage recorded in this window. Older records are available; choose a longer range or Show older recorded usage.":localSource?"No observed local usage yet. Send Ollama requests through the AIPULSE gateway to measure tokens; loading a model alone does not consume recorded tokens.":service.Settings.ReadAgentUsage?"No readable tool usage recorded yet. Codex / Claude Code must write supported token metadata; desktop Chat usage may not be available.":"Local usage import is disabled in Settings. Retained records remain available when present.";
        stack.Children.Add(TokenSection("On-prem models",onPrem,"Ollama usage observed through the AIPULSE gateway, plus optional local assistant usage. Direct Ollama calls and discovery-only remote endpoints are not counted.",Empty(true)));
        stack.Children.Add(TokenSection("Tools & other usage",tools,"Codex and Claude Code usage reported in readable local session files. Tool names identify the usage source; they do not prove where the model is hosted.",Empty(false)));
        stack.Children.Add(Message("Reading the split","Input includes cache reads and cache writes. Reasoning is included in output. Total = input + output; do not add the subset columns again. — means there are no observations in the selected window, not measured zero consumption. A zero category means no tokens were recorded for that field, which can also mean the provider did not report it. Records are usage events, not a request count."));
        stack.Children.Add(Message("Coverage","On-prem and tool totals are independent observations: a tool using a monitored Ollama backend may appear in both, so no combined total is presented. No API billing or desktop subscription allowance is inferred. Claude desktop/Chat/Cowork usage may not be available. Remote k3s discovery alone does not provide token accounting."));
        var export=Btn(tokenExportBusy?"Exporting…":"Export selected period CSV");export.IsEnabled=!tokenExportBusy;
        export.Click+=async(_,_)=>
        {
            var exportStore=service.Store;var exportHost=service.HostName;tokenExportBusy=true;long exportUntil=J.Now,exportSince=TokenWindowStart(exportUntil);RenderBody();
            try{var file=System.IO.Path.Combine(Paths.Root,"exports","tokens-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".csv");await Task.Run(()=>exportStore.ExportUsage(file,exportSince,exportUntil));tokenNotice="Exported "+exportHost+" to "+file;}
            catch(Exception e){Paths.Log("token-export",e);tokenNotice="Export failed. Check folder access and available disk space.";}
            finally{tokenExportBusy=false;if(page=="Token")RenderBody();}
        };
        stack.Children.Add(export);if(tokenNotice!="")stack.Children.Add(Text(tokenNotice,12,"#59dfae"));return stack;
    }
    static Control TokenSection(string title,List<TokenBreakdown> rows,string coverage,string empty)
    {
        var section=new StackPanel{Spacing=12};section.Children.Add(Text(title,16,null,true));section.Children.Add(Text(coverage,12,"#9eb1ca"));
        if(rows.Count==0){section.Children.Add(Text(empty,13,"#8da1bd"));return Card(section);}
        var totals=new StackPanel{Spacing=7};
        totals.Children.Add(Text($"Input {rows.Sum(r=>r.Input):N0} · uncached {rows.Sum(r=>r.Uncached):N0} · cache read {rows.Sum(r=>r.Cached):N0} · cache write {rows.Sum(r=>r.CacheWrite):N0}",13));
        totals.Children.Add(Text($"Output {rows.Sum(r=>r.Output):N0} · reasoning {rows.Sum(r=>r.Reasoning):N0} included · {rows.Sum(r=>r.Records):N0} usage records",13));section.Children.Add(totals);
        var table=Table(["Source / model","Input","Output","Cache read¹","Cache write¹","Reasoning²","Total"],rows.Select(r=>new[]{r.Source+"\n"+(string.IsNullOrWhiteSpace(r.Model)?"Model not reported":r.Model),r.Input.ToString("N0"),r.Output.ToString("N0"),r.Cached.ToString("N0"),r.CacheWrite.ToString("N0"),r.Reasoning.ToString("N0"),r.Total.ToString("N0")}),"2.2*,*,*,*,*,*,*");
        table.MinWidth=900;section.Children.Add(new ScrollViewer{Content=table,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        if(rows.Sum(r=>r.Incomplete)>0)section.Children.Add(Text($"{rows.Sum(r=>r.Incomplete):N0} records have incomplete or unavailable usage. Any reported tokens are included; this is not a complete consumption total.",12,"#e8b65f"));
        return Card(section);
    }
}
