using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiPulse;

public record CustomMetricWidget(string Id,string MetricId,string Label);

public sealed partial class MainWindow
{
    Border chatLauncher=null!;
    Avalonia.Controls.Shapes.Path chatPulse=null!;

    void InitializeChatLauncher(Grid surface)
    {
        chatPulse=new Avalonia.Controls.Shapes.Path{Data=Geometry.Parse("M1,12 H6 L9,5 L13,19 L16,12 H22"),
            Stroke=Brush.Parse("#49d5ed"),StrokeThickness=2,Width=22,Height=22,Stretch=Stretch.Uniform,IsHitTestVisible=false};
        var grip=new Border{Child=chatPulse,Background=Brushes.Transparent,Padding=new Thickness(9,4),Cursor=new Cursor(StandardCursorType.SizeAll),Tag=LayoutHandle};
        ToolTip.SetTip(grip,"Drag to move Ask Pulse");
        var open=Btn("Ask Pulse");
        open.Click+=(_,_)=>OpenChat();
        chatLauncher=new Border{HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,
            CornerRadius=new CornerRadius(18),BorderThickness=new Thickness(1),Padding=new Thickness(3),
            Child=new StackPanel{Orientation=Orientation.Horizontal,Children={grip,open}}};
        surface.Children.Add(chatLauncher);
        var prefs=localService.Settings;
        void Place(double right,double bottom)
        {
            chatLauncher.Margin=new Thickness(0,0,Math.Clamp(right,4,Math.Max(4,surface.Bounds.Width-chatLauncher.Bounds.Width-4)),
                Math.Clamp(bottom,34,Math.Max(34,surface.Bounds.Height-chatLauncher.Bounds.Height-4)));
        }
        surface.SizeChanged+=(_,_)=>Place(prefs.ChatLauncherRight??18,prefs.ChatLauncherBottom??46);
        Point origin=default;
        Thickness margin=default;
        IPointer? pointer=null;
        grip.PointerPressed+=(_,e)=>{
            if(!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)return;
            pointer=e.Pointer;origin=e.GetPosition(surface);margin=chatLauncher.Margin;
            pointer.Capture(grip);e.Handled=true;
        };
        grip.PointerMoved+=(_,e)=>{
            if(pointer!=e.Pointer)return;
            var now=e.GetPosition(surface);Place(margin.Right+origin.X-now.X,margin.Bottom+origin.Y-now.Y);
            e.Handled=true;
        };
        grip.PointerReleased+=(_,e)=>{
            if(pointer!=e.Pointer)return;
            pointer=null;e.Pointer.Capture(null);e.Handled=true;
            prefs.ChatLauncherRight=chatLauncher.Margin.Right;prefs.ChatLauncherBottom=chatLauncher.Margin.Bottom;
            Paths.Save(prefs);
        };
        grip.PointerCaptureLost+=(_,_)=>pointer=null;
        AutomationProperties.SetName(open,"Ask Pulse");
        var animation=new Avalonia.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(140)};
        animation.Tick+=(_,_)=>{if(chatLauncher.IsVisible&&IsVisible)chatPulse.Opacity=.78+.22*Math.Sin(Environment.TickCount64/250.0);};
        Closed+=(_,_)=>animation.Stop();
        animation.Start();
    }

    void StyleChatLauncher()
    {
        chatLauncher.IsVisible=!localService.Settings.Compact;
        chatLauncher.Background=Panel;chatLauncher.BorderBrush=Line;
        chatPulse.Stroke=Ink("#40d5ed");
        if(chatLauncher.Child is StackPanel row&&row.Children[1] is Button open)open.Background=Selected;
        if(chatWindow!=null)
        {
            chatWindow.Background=Canvas;chatWindow.Foreground=Ink(null);
            chatWindow.RequestedThemeVariant=RequestedThemeVariant;
            RefreshChat();
        }
    }

    static string NormalizeMetric(string text)=>Regex.Replace(text.ToLowerInvariant(),@"[^a-z0-9]+"," ").Trim();

    bool TryCreateWidget(string request,out string result)
    {
        result="";
        if(!Regex.IsMatch(request,@"\b(?:build|create|add|make|new)\b.*\b(?:widget|tile)\b",RegexOptions.IgnoreCase|RegexOptions.Singleline))return false;
        var dims=Regex.Match(request,@"(?<!\d)(\d+)\s*[x×*]\s*(\d+)(?!\d)",RegexOptions.IgnoreCase);
        int width=2,height=2;
        if(dims.Success&&(!int.TryParse(dims.Groups[1].Value,out width)||!int.TryParse(dims.Groups[2].Value,out height)||width is <1 or >4||height is <1 or >8))
        {result="Choose a widget size from 1×1 through 4×8.";return true;}
        string text=" "+NormalizeMetric(request)+" ";
        var aliases=new Dictionary<string,string>{
            ["cpu usage"]="cpu.load",["cpu utilization"]="cpu.load",["gpu usage"]="gpu.load",["gpu utilization"]="gpu.load",
            ["gpu temperature"]="gpu.temperature",["cpu temperature"]="cpu.temperature",["ram usage"]="ram.used",
            ["ram used"]="ram.used",["system memory"]="ram.used",["vram usage"]="gpu.local",["vram used"]="gpu.local"
        };
        string? id=aliases.Where(a=>text.Contains(" "+a.Key+" ",StringComparison.Ordinal)).OrderByDescending(a=>a.Key.Length).Select(a=>a.Value).FirstOrDefault();
        var readings=service.Readings.Where(r=>!string.IsNullOrWhiteSpace(r.Id)&&!string.IsNullOrWhiteSpace(r.Name)).ToList();
        var matches=readings.Where(r=>NormalizeMetric(r.Name).Length>=3&&text.Contains(" "+NormalizeMetric(r.Name)+" ",StringComparison.Ordinal))
            .OrderByDescending(r=>r.Name.Length).ToList();
        var exact=readings.Where(r=>NormalizeMetric(r.Id).Length>=3&&text.Contains(" "+NormalizeMetric(r.Id)+" ",StringComparison.Ordinal))
            .OrderByDescending(r=>r.Id.Length).FirstOrDefault();
        if(exact==null&&id==null&&matches.Count>1&&matches[0].Name.Length==matches[1].Name.Length&&matches[0].Id!=matches[1].Id)
        {result="Several readings match. Specify the exact metric ID or an unambiguous metric name from Sensors.";return true;}
        var reading=exact??(id!=null?readings.FirstOrDefault(r=>r.Id==id):matches.FirstOrDefault());
        if(id==null&&reading==null){result="I couldn't match a collected metric. Try GPU temperature, GPU utilization, RAM used, or the exact metric ID from Sensors.";return true;}
        id=reading?.Id??id!;
        var prefs=localService.Settings;
        prefs.CustomMetricWidgets??=[];
        if(prefs.CustomMetricWidgets.Count>=32){result="The custom widget shelf has reached its 32-widget limit.";return true;}
        string label=reading?.Name??aliases.First(a=>a.Value==id).Key;
        string key="custom:"+Guid.NewGuid().ToString("N");
        prefs.CustomMetricWidgets.Add(new(key,id,label));
        (prefs.OverviewTileSizes??=[])[key]=new(width,height);
        Paths.Save(prefs);
        result=$"Added {label} ({width}×{height}) to Overview → Edit layout → Widget shelf.";
        return true;
    }
}
