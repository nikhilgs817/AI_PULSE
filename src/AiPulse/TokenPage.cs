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
    Control TokenPage()
    {
        var stack=new StackPanel{Spacing=18};
        stack.Children.Add(Heading("Token","Observed token usage · on-prem models and tools shown separately"));
        tokenDays=Math.Min(tokenDays,service.Settings.RetentionDays);
        var periods=new WrapPanel{Orientation=Orientation.Horizontal};
        foreach(int days in new[]{1,7,30,90,180,365}.Where(d=>d<=service.Settings.RetentionDays).Append(service.Settings.RetentionDays).Distinct().Order())
        {
            var b=Btn(days==1?"Last 24 hours":$"{days} days");b.Margin=new Thickness(0,0,8,8);if(days==tokenDays)b.Background=Brush.Parse("#443477");
            b.Click+=(_,_)=>{tokenDays=days;RenderBody();};periods.Children.Add(b);
        }
        stack.Children.Add(periods);
        var rows=service.Store.TokenBreakdown(J.Now-tokenDays*86400L);
        var onPrem=rows.Where(r=>r.OnPrem).ToList();var tools=rows.Where(r=>!r.OnPrem).ToList();
        var summary=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*")};
        var local=Metric("On-prem · Ollama",onPrem.Sum(r=>r.Total).ToString("N0"),$"Input {onPrem.Sum(r=>r.Input):N0} · output {onPrem.Sum(r=>r.Output):N0}");summary.Children.Add(local);
        var other=Metric("Tools & other usage",tools.Sum(r=>r.Total).ToString("N0"),$"Input {tools.Sum(r=>r.Input):N0} · output {tools.Sum(r=>r.Output):N0}");other.Margin=new Thickness(12,0,0,0);Grid.SetColumn(other,1);summary.Children.Add(other);stack.Children.Add(summary);
        stack.Children.Add(TokenSection("On-prem models",onPrem,"Ollama usage observed through the AIPULSE gateway. Direct Ollama calls and discovery-only remote endpoints are not counted."));
        stack.Children.Add(TokenSection("Tools & other usage",tools,"Codex and Claude Code usage reported in readable local session files. Tool names identify the usage source; they do not prove where the model is hosted."));
        stack.Children.Add(Message("Reading the split","Input includes cache reads and cache writes. Reasoning is included in output. Total = input + output; do not add the subset columns again. Zero means no tokens recorded for that field, which can also mean the provider did not report it. Records are usage events, not a request count."));
        stack.Children.Add(Message("Coverage","On-prem and tool totals are independent observations: a tool using a monitored Ollama backend may appear in both, so no combined total is presented. No API billing or desktop subscription allowance is inferred. Claude desktop/Chat/Cowork usage may not be available. Remote k3s discovery alone does not provide token accounting."));
        var export=Btn(tokenExportBusy?"Exporting…":"Export selected period CSV");export.IsEnabled=!tokenExportBusy;
        export.Click+=async(_,_)=>
        {
            tokenExportBusy=true;long since=J.Now-tokenDays*86400L;RenderBody();
            try{var file=System.IO.Path.Combine(Paths.Root,"exports","tokens-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".csv");await Task.Run(()=>service.Store.ExportUsage(file,since));tokenNotice="Saved "+file;}
            catch(Exception e){Paths.Log("token-export",e);tokenNotice="Export failed. Check folder access and available disk space.";}
            finally{tokenExportBusy=false;if(page=="Token")RenderBody();}
        };
        stack.Children.Add(export);if(tokenNotice!="")stack.Children.Add(Text(tokenNotice,12,"#59dfae"));return stack;
    }
    static Control TokenSection(string title,List<TokenBreakdown> rows,string coverage)
    {
        var section=new StackPanel{Spacing=12};section.Children.Add(Text(title,16,null,true));section.Children.Add(Text(coverage,12,"#9eb1ca"));
        if(rows.Count==0){section.Children.Add(Text("No observed usage in this period.",13,"#8da1bd"));return Card(section);}
        var totals=new StackPanel{Spacing=7};
        totals.Children.Add(Text($"Input {rows.Sum(r=>r.Input):N0} · uncached {rows.Sum(r=>r.Uncached):N0} · cache read {rows.Sum(r=>r.Cached):N0} · cache write {rows.Sum(r=>r.CacheWrite):N0}",13));
        totals.Children.Add(Text($"Output {rows.Sum(r=>r.Output):N0} · reasoning {rows.Sum(r=>r.Reasoning):N0} included · {rows.Sum(r=>r.Records):N0} usage records",13));section.Children.Add(totals);
        var table=Table(["Source / model","Input","Output","Cache read¹","Cache write¹","Reasoning²","Total"],rows.Select(r=>new[]{r.Source+"\n"+(string.IsNullOrWhiteSpace(r.Model)?"Model not reported":r.Model),r.Input.ToString("N0"),r.Output.ToString("N0"),r.Cached.ToString("N0"),r.CacheWrite.ToString("N0"),r.Reasoning.ToString("N0"),r.Total.ToString("N0")}),"2.2*,*,*,*,*,*,*");
        table.MinWidth=900;section.Children.Add(new ScrollViewer{Content=table,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        if(rows.Sum(r=>r.Incomplete)>0)section.Children.Add(Text($"{rows.Sum(r=>r.Incomplete):N0} records have incomplete or unavailable usage. Any reported tokens are included; this is not a complete consumption total.",12,"#e8b65f"));
        return Card(section);
    }
}
