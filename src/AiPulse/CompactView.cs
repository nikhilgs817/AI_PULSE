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

// The small always-on-top view: headline gauges, token counters and the loaded model list.
public sealed partial class MainWindow
{
    Control Compact()
    {
        bool vertical=localService.Settings.CompactVertical;
        int toggleRow = vertical ? 1 : 2;
        var rowSizes = vertical ? "221" : "52,60";
        if (ShowCompactQuotas) rowSizes += $",{CompactQuotaRowHeight}";
        var grid=new Grid{RowDefinitions=new RowDefinitions(rowSizes + ",26,Auto"),Margin=new Thickness(8,0)};
        var metrics=new Grid{ColumnDefinitions=new ColumnDefinitions(vertical?"*":"*,*,*,*"),RowDefinitions=new RowDefinitions(vertical?string.Join(",",Enumerable.Repeat("24.5",9)):"*")};
        bool fresh=service.Fresh;
        string freshness=!fresh?(service.LastSample==0?"Waiting":"Stale"):service.IsRemote?"Live":$"Live {Math.Max(0,J.Now-service.LastSample)}s";
        string Live(string id)=>fresh?Format(id):"—";
        var hottest=fresh?service.Readings
            .Where(r=>(r.Id is "cpu.temperature" or "gpu.temperature")&&r.Value is double v&&double.IsFinite(v)&&v>0&&v<=150)
            .MaxBy(r=>r.Value):null;
        var soc=fresh&&hottest==null?service.Readings.FirstOrDefault(r=>r.Name=="GPU VR SoC"&&r.Unit=="°C"&&r.Value is double v&&v>0&&v<=150):null;
        string hotSource=hottest?.Id=="cpu.temperature"?"CPU":"GPU";
        bool hot=hottest?.Value>=service.Settings.TemperatureWarning;
        double? localUsed=fresh?service.Value("gpu.local"):null,localTotal=fresh?service.Value("gpu.local.total"):null;
        string gpuMemory=localUsed is double used&&localTotal is double total&&total>0?$"{used:0.#}/{total:0.#}":J.Size(localUsed);
        (string Label,string Value,string Help,bool Warn)[] fields=[
            ("GPU LOAD",Live("gpu.load"),"Current GPU utilization. "+freshness,false),
            ("GPU LOCAL GiB",gpuMemory,$"Local allocation: {J.Size(localUsed)} of {J.Size(localTotal)} reported local capacity. Shared allocation: {Live("gpu.shared")}; reported shared limit: {Live("gpu.shared.total")}. Shared GPU memory overlaps system RAM. These numbers do not predict model fit.",false),
            ("RAM FREE",Live("ram.available"),"Available physical memory, including reclaimable memory reported by the OS. "+freshness,false),
            (hottest==null?(soc==null?"HOTTEST":"GPU VR SOC"):$"HOTTEST · {hotSource}",hottest==null?(soc==null?"—":Format(soc.Id)):Format(hottest.Id),hottest==null?(soc==null?"No fresh CPU or GPU core temperature. "+SensorDescription():"Fallback sensor only: GPU VR SoC is not GPU core temperature."):$"{hottest.Name}: {Format(hottest.Id)}. Warning threshold: {service.Settings.TemperatureWarning:0}°C.",hot)
        ];
        for(int i=0;i<fields.Length;i++)
        {
            var field=fields[i];
            var label=Text(field.Label,vertical?12:10,"#a9bdd9",true);
            var value=Text(field.Value,vertical?17:18,field.Warn?"#ffc58a":"#e9f0ff",true);
            var cell=new Grid{Margin=new Thickness(6,vertical?0:5),RowDefinitions=new RowDefinitions(vertical?"*":"Auto,*"),ColumnDefinitions=new ColumnDefinitions(vertical?"*,Auto":"*")};cell.Children.Add(label);
            if(vertical)Grid.SetColumn(value,1);else Grid.SetRow(value,1);cell.Children.Add(value);
            ToolTip.SetTip(cell,field.Help);
            if(vertical)Grid.SetRow(cell,i);else Grid.SetColumn(cell,i);metrics.Children.Add(cell);
        }
        grid.Children.Add(metrics);
        long until=J.Now,since=J.DayStart(DateTimeOffset.FromUnixTimeSeconds(until),TimeZoneInfo.Local);
        var activity=service.Store.CompactActivity(since,until);
        var tokens=TokenSummary.From(service.Store.TokenBreakdown(since,until),service.Store.CounterTotals(since,until));
        var spawns=service.Store.AgentSpawns(since,until);
        Control ActivityCell(string title,string value,string help)
        {
            var cell=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(6,2)};
            var label=Text(title,vertical?12:11,"#a9bdd9",true);label.FontWeight=FontWeight.Bold;cell.Children.Add(label);
            var number=Text(value,vertical?19:16,"#e9f0ff",true);Grid.SetColumn(number,1);cell.Children.Add(number);ToolTip.SetTip(cell,help);Avalonia.Automation.AutomationProperties.SetName(cell,title+": "+value);return cell;
        }
        var agents=ActivityCell("ACTIVE AGENTS",service.Settings.ReadAgentUsage&&(!service.IsRemote||service.Fresh)?activity.ActiveAgents.ToString("N0"):"—",service.Settings.ReadAgentUsage?"Sessions reporting Working or Recent activity, plus agents named in model requests or agent spans, within the last 5 minutes. Completed, interrupted and quiet sessions are excluded. This does not prove the model is generating right now.":"Agent usage import is disabled. Enable Read local usage metadata in Settings to count recent active sessions.");
        const string overlap="Observed input and output since local midnight. Local and agent sources may overlap; do not add them. Open Token for details.";
        var local=ActivityCell("LOCAL TOKENS TODAY",CompactCount(tokens.LocalRequests),overlap+" Includes observed Ollama and AIPULSE Assistant requests.");
        var tools=ActivityCell("AGENT TOKENS TODAY",CompactCount(tokens.ToolRequests),overlap);
        var vllm=ActivityCell("vLLM TOKENS TODAY",CompactCount(tokens.VllmCounters),"Server counters observed since local midnight. Open Token to inspect each model and replica.");
        var spawned=ActivityCell("AGENT SPAWNS TODAY",spawns.Count==0?"—":J.Count(spawns.Sum(s=>s.Count)),"Distinct agent starts observed since local midnight. Open Agents to see the sources.");
        Control[] activityCells=[agents,local,tools,spawned,vllm];
        if(vertical)
        {
            for(int i=0;i<activityCells.Length;i++){Grid.SetRow(activityCells[i],fields.Length+i);metrics.Children.Add(activityCells[i]);}
        }
        else
        {
            var activityRows=new UniformGrid{Columns=3,Rows=2};
            foreach(var cell in activityCells)activityRows.Children.Add(cell);
            Grid.SetRow(activityRows,1);grid.Children.Add(activityRows);
        }
        if (ShowCompactQuotas)
        {
            var quotas = SubscriptionQuotaRow(compact: true);
            Grid.SetRow(quotas, toggleRow++);
            grid.Children.Add(quotas);
        }
        string modelStatus=fresh?$"{service.Ollama.Active.Values.Sum(n=>(long)n)} requests · {service.Ollama.Models.Count} loaded":"Models unavailable";
        var toggle=Btn((modelsExpanded?"▴":"▾")+"  "+modelStatus+" · "+freshness);ToolTip.SetTip(toggle,"Observed active gateway requests and resident models. Direct inference may not be counted. Select to show the model list.");toggle.Padding=new Thickness(6,2);toggle.MinHeight=24;toggle.FontSize=11;toggle.HorizontalAlignment=HorizontalAlignment.Stretch;toggle.HorizontalContentAlignment=HorizontalAlignment.Left;toggle.Background=Brush.Parse("#0cffffff");toggle.CornerRadius=new CornerRadius(12);
        Avalonia.Automation.AutomationProperties.SetName(toggle,"Toggle loaded models");toggle.Click+=(_,_)=>{modelsExpanded=!modelsExpanded;RenderBody();KeepOnScreen();};Grid.SetRow(toggle,toggleRow);grid.Children.Add(toggle);
        if(modelsExpanded)
        {
            var list=new StackPanel{Spacing=5,Margin=new Thickness(0,3,0,6)};
            if(service.Ollama.Models.Count==0)list.Children.Add(Text(service.Ollama.Status.GetValueOrDefault("This PC")=="Connected"?"Ollama is ready. No models loaded.":"Waiting for Ollama…",11,"#8ea3c0"));
            var rates=service.Store.UsageSince(J.Now-3600).Where(u=>u.Source=="Ollama"&&u.DecodeRate.HasValue).GroupBy(u=>u.Model).ToDictionary(g=>g.Key,g=>g.First());
            foreach(var m in service.Ollama.Models.OrderBy(m=>m.Host).ThenBy(m=>m.Name))
            {
                var row=new StackPanel{Spacing=4,Margin=new Thickness(8,7)};row.Children.Add(Text(m.Name,12,null,true));
                row.Children.Add(Text(ModelHost(m)+" · "+m.State+" · "+J.Size(m.MemoryGiB),10,"#a7bad4"));
                row.Children.Add(Text(m.Host=="This PC"&&rates.TryGetValue(m.Name,out var u)?$"{u.DecodeRate:0.0} tok/s · last observed request":"Decode rate unavailable",10,"#55cee5"));
                list.Children.Add(new Border{Child=row,Background=Brush.Parse("#16ffffff"),BorderBrush=Brush.Parse("#38e1edff"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14)});
            }
            var scroll=new ScrollViewer{Content=list,Height=CompactHeight()-CompactBaseHeight,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Offset=compactOffset};scroll.ScrollChanged+=(_,_)=>compactOffset=scroll.Offset;Grid.SetRow(scroll,toggleRow+1);grid.Children.Add(scroll);
        }
        return grid;
    }
    static string CompactCount(long? count)=>count is long measured?J.Count(measured):"—";
    string ModelHost(ModelInfo model)=>service.IsRemote&&model.Host=="This PC"?service.HostName:model.Host;
}
