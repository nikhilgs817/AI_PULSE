using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiPulse;
public sealed class TrendChart : Control
{
    public List<MetricPoint> Points{get;set;}=[];
    public double? MaxValue{get;set;}=100;
    public string Unit{get;set;}="%";
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);double width=Bounds.Width,height=Bounds.Height;if(width<60||height<60)return;
        double left=38,right=width-12,top=14,bottom=height-28;
        var muted=Brush.Parse("#7588a4");var pen=new Pen(Brush.Parse("#28364b"),1);
        double max=MaxValue??Math.Max(1,Points.Count==0?1:Points.Max(p=>p.Maximum)*1.15);
        void Text(string value,double x,double y)=>ctx.DrawText(new FormattedText(value,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,muted),new Point(x,y));
        for(int i=0;i<=2;i++){double y=top+(bottom-top)*i/2;ctx.DrawLine(pen,new Point(left,y),new Point(right,y));Text((max*(1-i/2.0)).ToString("0.#"),2,y-6);}
        Text(Unit,2,0);
        if(Points.Count<2){Text("Collecting history…",left+12,top+30);return;}
        long first=Points.First().Time,last=Points.Last().Time;double range=Math.Max(1,last-first);
        Point XY(MetricPoint p)=>new(left+(p.Time-first)/range*(right-left),bottom-Math.Clamp(p.Average/max,0,1)*(bottom-top));
        var intervals=Points.Zip(Points.Skip(1),(a,b)=>b.Time-a.Time).Where(t=>t>0).Order().ToArray();double gap=intervals.Length>0?intervals[intervals.Length/2]*1.5:180;
        for(int i=1;i<Points.Count;i++)
        {
            if(Points[i].Time-Points[i-1].Time>gap)continue;
            var a=XY(Points[i-1]);var b=XY(Points[i]);var geometry=new StreamGeometry();using(var g=geometry.Open()){g.BeginFigure(new Point(a.X,bottom),true);g.LineTo(a);g.LineTo(b);g.LineTo(new Point(b.X,bottom));g.EndFigure(true);}
            ctx.DrawGeometry(Brush.Parse("#143447"),null,geometry);ctx.DrawLine(new Pen(Brush.Parse("#40d5ed"),2),a,b);
        }
        Text(DateTimeOffset.FromUnixTimeSeconds(first).ToLocalTime().ToString(range>86400?"MMM d":"HH:mm"),left,bottom+8);
        Text(DateTimeOffset.FromUnixTimeSeconds(last).ToLocalTime().ToString(range>86400?"MMM d":"HH:mm"),right-38,bottom+8);
    }
}
