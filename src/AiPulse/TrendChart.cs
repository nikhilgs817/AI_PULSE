using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiPulse;
public record ChartSeries(string Name,List<MetricPoint> Points,string Color);

public sealed class TrendChart : Control
{
    public List<MetricPoint> Points{get;set;}=[];
    public List<ChartSeries> Series{get;set;}=[];
    public double? MaxValue{get;set;}=100;
    public string Unit{get;set;}="%";
    public long? WindowStart{get;set;}
    public long? WindowEnd{get;set;}
    public TimeZoneInfo Zone{get;set;}=TimeZoneInfo.Local;
    public static string TimeLabel(long time,TimeZoneInfo zone)=>TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(time),zone).ToString("yyyy-MM-dd\nHH:mm");
    public static long[] TimeTicks(long first,long last,int count)=>Enumerable.Range(0,count).Select(i=>first+(long)((last-first)*(double)i/(count-1))).ToArray();
    List<ChartSeries> Lines=>Series.Count>0?Series:[new("Value",Points,"#40d5ed")];
    public TrendChart()
    {
        PointerMoved+=(_,e)=>
        {
            var lines=Lines;var all=lines.SelectMany(s=>s.Points).ToList();if(all.Count==0)return;
            long first=WindowStart??all.Min(p=>p.Time),last=WindowEnd??all.Max(p=>p.Time);
            double fraction=Math.Clamp((e.GetPosition(this).X-48)/Math.Max(1,Bounds.Width-64),0,1);
            long target=first+(long)((last-first)*fraction);var text=new List<string>();
            foreach(var line in lines.Where(s=>s.Points.Count>0))
            {
                var point=line.Points.MinBy(p=>Math.Abs(p.Time-target))!;
                var intervals=line.Points.Zip(line.Points.Skip(1),(a,b)=>b.Time-a.Time).Where(d=>d>0).Order().ToArray();
                long tolerance=intervals.Length>0?intervals[intervals.Length/2]:60;
                if(Math.Abs(point.Time-target)>tolerance)continue;
                var at=TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(point.Time),Zone);
                text.Add($"{line.Name}: {point.Average:0.##} {Unit} · {at:yyyy-MM-dd HH:mm:ss zzz}");
            }
            ToolTip.SetTip(this,text.Count>0?string.Join("\n",text):"No observation near this time");
        };
    }
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);double width=Bounds.Width,height=Bounds.Height;if(width<140||height<100)return;
        double left=48,right=width-16,top=25,bottom=height-64;
        var muted=Brush.Parse("#8ea3bf");var pen=new Pen(Brush.Parse("#28364b"),1);var lines=Lines;
        var points=lines.SelectMany(s=>s.Points).Where(p=>double.IsFinite(p.Average)).ToList();
        double max=MaxValue??Math.Max(1,points.Count==0?1:points.Max(p=>p.Maximum)*1.15);
        void Label(string value,double x,double y,bool centered=false)
        {
            var text=new FormattedText(value,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,muted);
            if(centered)x=Math.Clamp(x-text.Width/2,0,Math.Max(0,width-text.Width));ctx.DrawText(text,new Point(x,y));
        }
        for(int i=0;i<=4;i++){double y=top+(bottom-top)*i/4;ctx.DrawLine(pen,new Point(left,y),new Point(right,y));Label((max*(1-i/4.0)).ToString("0.#"),2,y-6);}
        Label(Unit,2,4);
        long last=WindowEnd??(points.Count>0?points.Max(p=>p.Time):J.Now);
        long first=WindowStart??(points.Count>1?points.Min(p=>p.Time):last-900);
        if(last<=first)last=first+60;
        foreach(var tick in TimeTicks(first,last,width>=500?5:3))
        {
            double x=left+(double)(tick-first)/(last-first)*(right-left);
            ctx.DrawLine(pen,new Point(x,top),new Point(x,bottom));Label(TimeLabel(tick,Zone),x,bottom+8,true);
        }
        Label("Date / time · "+Zone.Id,(left+right)/2,height-16,true);
        foreach(var series in lines)
        {
            var values=series.Points.Where(p=>p.Time>=first&&p.Time<=last&&double.IsFinite(p.Average)).OrderBy(p=>p.Time).ToList();
            var brush=Brush.Parse(series.Color);var linePen=new Pen(brush,2);
            Point XY(MetricPoint p)=>new(left+(double)(p.Time-first)/(last-first)*(right-left),bottom-Math.Clamp(p.Average/max,0,1)*(bottom-top));
            var intervals=values.Zip(values.Skip(1),(a,b)=>b.Time-a.Time).Where(t=>t>0).Order().ToArray();double gap=intervals.Length>0?intervals[intervals.Length/2]*1.5:180;
            using(ctx.PushClip(new Rect(left,top,right-left,bottom-top)))
            {
                for(int i=1;i<values.Count;i++)if(values[i].Time-values[i-1].Time<=gap)ctx.DrawLine(linePen,XY(values[i-1]),XY(values[i]));
                foreach(var p in values)ctx.DrawEllipse(brush,null,XY(p),2,2);
            }
        }
        if(points.Count==0)Label("Collecting history…",left+12,top+30);
    }
}
