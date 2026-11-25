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

public sealed partial class MainWindow : Window
{
    readonly MonitorService localService;
    FleetDashboard? fleet;
    MonitorService service=>fleet?.SelectedMonitor??localService;
    readonly Grid shell=new();
    readonly ContentControl header=new(),body=new();
    readonly TextBlock footer=new(){Text="Starting collectors…",FontSize=10,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly ContentControl footerActions=new(){VerticalAlignment=VerticalAlignment.Center};
    readonly Border footerBar=new();
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly Dictionary<string,string> searches=[];
    string page="Overview",notice="";
    string? layoutPage;
    bool firstPosition=true,modelsExpanded,rebuilding;
    IPointer? compactMovePointer;
    PixelPoint compactMoveOrigin,compactWindowOrigin;
    int updates;
    ScrollViewer? scroller;
    Vector compactOffset;
    TextBlock? settingsSensorStatus;
    public MainWindow(MonitorService monitor)
    {
        localService=monitor;InitializeFleet();Title="AI Pulse";Background=Canvas;Foreground=Brush.Parse("#e9effa");FontFamily="Segoe UI";
        int pi=Array.IndexOf(Program.Arguments,"--page");if(pi>=0&&Program.Arguments.Length>pi+1)page=Program.Arguments[pi+1];
        if(page=="Assistant")page="Settings";
        Topmost=localService.Settings.Pinned;ExtendClientAreaToDecorationsHint=true;WindowDecorations=WindowDecorations.BorderOnly;ExtendClientAreaTitleBarHeightHint=0;
        try{Icon=new WindowIcon(AssetLoader.Open(new Uri("avares://AiPulse/Assets/pulse.ico")));}catch{}
        shell.Children.Add(header);Grid.SetRow(body,1);shell.Children.Add(body);
        var footerGrid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8,Children={footer,footerActions}};
        Grid.SetColumn(footerActions,1);
        footerBar.Child=footerGrid;footerBar.Padding=new Thickness(14,0);
        footerBar.BorderThickness=new Thickness(0,1,0,0);
        Grid.SetRow(footerBar,2);shell.Children.Add(footerBar);InitializeGlassChrome();
        // A transparent hit surface lets gaps in the glass move the window too.
        shell.Background=Brushes.Transparent;
        // Handle the entire non-interactive surface; routed button/input events retain their normal behavior.
        AddHandler(PointerPressedEvent,(_,e)=>
        {
            if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;
            for(var v=e.Source as Visual;v!=null&&v!=this;v=v.GetVisualParent())
                if(v is Button or TextBox or ComboBox or Slider or ScrollBar or NumericUpDown or ToggleSwitch or MenuItem||
                    v is Control control&&ReferenceEquals(control.Tag,LayoutHandle))return;
            if(!localService.Settings.Compact&&e.ClickCount==2&&e.GetPosition(this).Y<header.Bounds.Bottom){e.Handled=true;Dispatcher.UIThread.Post(ToggleMaximize);return;}
            if(!localService.Settings.Compact||!OperatingSystem.IsWindows()){BeginMoveDrag(e);return;}
            if(compactMovePointer!=null)return;
            compactMovePointer=e.Pointer;
            compactMoveOrigin=this.PointToScreen(e.GetPosition(this));
            compactWindowOrigin=Position;
            e.Pointer.Capture(this);
            e.Handled=true;
        },RoutingStrategies.Bubble);
        PointerMoved+=(_,e)=>
        {
            if(e.Pointer!=compactMovePointer)return;
            var point=this.PointToScreen(e.GetPosition(this));
            var destination=new PixelPoint(compactWindowOrigin.X+point.X-compactMoveOrigin.X,
                compactWindowOrigin.Y+point.Y-compactMoveOrigin.Y);
            if(Position!=destination)Position=destination;
            e.Handled=true;
        };
        PointerReleased+=(_,e)=>
        {
            if(e.Pointer!=compactMovePointer)return;
            FinishCompactMove();
            e.Pointer.Capture(null);
            e.Handled=true;
        };
        PointerCaptureLost+=(_,e)=>{if(e.Pointer==compactMovePointer)FinishCompactMove();};
        // Keep the pressed visual attached until native pointer processing finishes.
        PropertyChanged+=(_,e)=>{if(e.Property==WindowStateProperty)Dispatcher.UIThread.Post(BuildHeader);};
        Closing+=(_,e)=>{if(Application.Current is App a&&!a.Quitting){SavePosition();if(OperatingSystem.IsWindows()){e.Cancel=true;Hide();}else{a.Quitting=true;(a.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}}};
        Opened+=async(_,_)=>
        {
            if(firstPosition)
            {
                firstPosition=false;
                var screen=Screens.Primary??Screens.All.FirstOrDefault();
                if(screen!=null)
                {
                    var area=screen.WorkingArea;
                    var position=new PixelPoint((int)(localService.Settings.X??(area.Right-Width*screen.Scaling-20)),
                        (int)(localService.Settings.Y??(area.Bottom-Height*screen.Scaling-48)));
                    // Restore using the saved coordinate's monitor, before the native position update.
                    Position=ConstrainPosition(position);
                }
            }
            if(!localService.Settings.FleetDashboard&&(Program.Arguments.Contains("--enable-sensors")||service.Settings.RequestSensorsAtLaunch))service.SensorBridge.Enable();
            if(Program.Arguments.Contains("--ui-test")){await Task.Delay(5000);await RunUiChecks();return;}
            if(Program.Arguments.Contains("--capture")){await Task.Delay(12000);Capture();}
            int exitIndex=Array.IndexOf(Program.Arguments,"--exit-after");if(exitIndex>=0&&Program.Arguments.Length>exitIndex+1&&int.TryParse(Program.Arguments[exitIndex+1],out int seconds)){await Task.Delay(Math.Clamp(seconds,1,3600)*1000);Quit();}
        };
        KeyDown+=(_,e)=>{if(e.Key==Key.D&&e.KeyModifiers.HasFlag(KeyModifiers.Control))ToggleMode();if(e.Key==Key.Q&&e.KeyModifiers.HasFlag(KeyModifiers.Control))Quit();};
        ApplyWindowLayout();timer.Tick+=(_,_)=>TimerTick();timer.Start();Closed+=(_,_)=>{fleet?.Dispose();assistantRequest?.Cancel();assistant.Dispose();};Refresh();
    }
    void FinishCompactMove()
    {
        compactMovePointer=null;
        if(Position!=compactWindowOrigin)SavePosition();
    }
    void SavePosition()
    {
        if(WindowState!=WindowState.Normal)return;
        localService.Settings.X=Position.X;localService.Settings.Y=Position.Y;
        Paths.Save(localService.Settings);
    }
    void Quit(){SavePosition();if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}}
    void KeepOnScreen()=>Position=ConstrainPosition(Position);
    PixelPoint ConstrainPosition(PixelPoint position)
    {
        var screen=Screens.All.FirstOrDefault(s=>s.Bounds.Contains(position))
            ??Screens.ScreenFromWindow(this)??Screens.Primary;
        return screen==null?position:FitPosition(position,screen.WorkingArea,(int)(Width*screen.Scaling),(int)(Height*screen.Scaling));
    }
    internal static PixelPoint FitPosition(PixelPoint position,PixelRect area,int width,int height)=>new(
        Math.Clamp(position.X,area.X,Math.Max(area.X,area.Right-width)),
        Math.Clamp(position.Y,area.Y,Math.Max(area.Y,area.Bottom-height)));
    void ApplyWindowLayout()
    {
        bool compact=localService.Settings.Compact,vertical=localService.Settings.CompactVertical;
        ApplyPalette();
        if(compact&&WindowState==WindowState.Maximized)WindowState=WindowState.Normal;
        MinWidth=compact?(vertical?240:560):850;MinHeight=compact?100:520;CanResize=!compact;CanMaximize=!compact;
        shell.RowDefinitions=new RowDefinitions(compact?(fleet!=null?"62,*,0":"34,*,0"):"52,*,34");footerBar.IsVisible=!compact;
        Width=compact?(vertical?286:560):1280;
        Height=compact?CompactHeight():820;
        BuildHeader();layoutPage=null;
    }
    const int CompactQuotaRowHeight = 52;
    bool ShowCompactQuotas => fleet is null && !service.IsRemote;
    double CompactBaseHeight=>(localService.Settings.CompactVertical?281:172)+(fleet!=null?28:0)+(ShowCompactQuotas?CompactQuotaRowHeight:0);
    double CompactHeight()=>CompactBaseHeight+(modelsExpanded?Math.Min(300,Math.Max(66,service.Ollama.Models.Count*(localService.Settings.CompactVertical?94:76)+12)):0);
    void BuildHeader()
    {
        bool compact=localService.Settings.Compact;
        var title=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        var brand=new Border{Width=compact?(localService.Settings.CompactVertical?88:124):195,Height=compact?22:34,HorizontalAlignment=HorizontalAlignment.Left,ClipToBounds=true,Child=new Image{Source=new Bitmap(AssetLoader.Open(new Uri("avares://AiPulse/Assets/wordmark.png"))),Stretch=Stretch.UniformToFill}};
        Avalonia.Automation.AutomationProperties.SetName(brand,"AIPULSE · drag to move");title.Children.Add(brand);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=compact?3:6,VerticalAlignment=VerticalAlignment.Center};
        Button Action(string label,string help,Action action){var b=Btn(label);b.FontSize=compact?11:13;b.Padding=compact?new Thickness(5,2):new Thickness(10,7);b.MinHeight=compact?24:34;if(compact)StyleGlassButton(b);ToolTip.SetTip(b,help);Avalonia.Automation.AutomationProperties.SetName(b,help);b.Click+=(_,_)=>action();controls.Children.Add(b);return b;}
        if(compact)Action(LightTheme?"☀":"☾","Toggle compact theme",()=>TogglePalette(true));
        else
        {
            var theme=new ToggleSwitch{IsChecked=LightTheme,OnContent="Light",OffContent="Dark",FontSize=11,
                VerticalAlignment=VerticalAlignment.Center};
            Avalonia.Automation.AutomationProperties.SetName(theme,"Toggle dashboard theme");
            ToolTip.SetTip(theme,"Dashboard appearance");
            theme.IsCheckedChanged+=(_,_)=>TogglePalette(false);
            controls.Children.Add(theme);
        }
        Action(Topmost?"●":"○",Topmost?"Unpin window":"Pin window on top",()=>{Topmost=!Topmost;localService.Settings.Pinned=Topmost;Paths.Save(localService.Settings);BuildHeader();});
        if(compact)Action(localService.Settings.CompactVertical?"↔":"↕","Switch compact orientation",()=>{localService.Settings.CompactVertical=!localService.Settings.CompactVertical;Paths.Save(localService.Settings);ApplyWindowLayout();RenderBody();KeepOnScreen();});
        Action(compact?"↗":"Compact ↙",compact?"Expand dashboard":"Show compact dashboard",ToggleMode);
        Action("—","Minimize",()=>WindowState=WindowState.Minimized);
        if(!compact)Action(WindowState==WindowState.Maximized?"❐":"□",WindowState==WindowState.Maximized?"Restore window":"Maximize window",ToggleMaximize);
        Action("×",OperatingSystem.IsWindows()?"Close to tray":"Close window",Close);
        Grid.SetColumn(controls,1);title.Children.Add(controls);
        AddFleetHeader(title,compact);
        header.Content=new Border{Child=title,Padding=compact?new Thickness(8,3):new Thickness(14,6),Background=compact?Brush.Parse("#0dffffff"):Brushes.Transparent,BorderBrush=compact?Brush.Parse("#22d4eaff"):Line,BorderThickness=new Thickness(0,0,0,1)};
    }
    void ToggleMaximize(){if(!localService.Settings.Compact)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;}
    public void ToggleMode(){SavePosition();localService.Settings.Compact=!localService.Settings.Compact;Paths.Save(localService.Settings);ApplyWindowLayout();RenderBody();KeepOnScreen();}
    void TimerTick()=>Refresh();
    void Refresh()
    {
        if(overviewDragPending||overviewShelfOpen||!IsVisible&&updates++>0)return;
        if(fleet!=null){UpdateFooter(fleet.Status+" · "+SelectedHostLabel());if(this.GetVisualDescendants().OfType<ComboBox>().Any(c=>c.IsDropDownOpen))return;RefreshHostMenu();if(layoutPage==null||page is not ("Settings" or "Assistant"))RenderBody();return;}
        int alerts;
        try{alerts=service.Store.Alerts().Count(a=>a.Active);}
        catch(Microsoft.Data.Sqlite.SqliteException e)when(e.SqliteErrorCode is 11 or 26){ShowDatabaseError();return;}
        UpdateFooter($"{alerts} alerts · "+(service.Settings.LastBackupError!=null?"Backup needs attention":service.Settings.LastBackup!=null?"Backup verified":"Backup pending"));
        if(settingsSensorStatus!=null)settingsSensorStatus.Text=SensorDescription();
        if(this.GetVisualDescendants().OfType<ComboBox>().Any(c=>c.IsDropDownOpen))return;
        if(layoutPage==null||page is not ("Settings" or "Assistant")||localService.Settings.Compact)RenderBody();
    }
    string Query=>searches.GetValueOrDefault(page,"");
    bool Matches(params string[] values)=>string.IsNullOrWhiteSpace(Query)||values.Any(v=>v.Contains(Query.Trim(),StringComparison.OrdinalIgnoreCase));
    void RenderBody()
    {
        if(rebuilding)return;rebuilding=true;
        try
        {
            if(localService.Settings.Compact)
            {
                if(compactMovePointer!=null)return;
                Reconciler.Show(body,fleet!=null&&fleet.SelectedHost==""?FleetCompact():fleet!=null&&fleet.SelectedMonitor?.RemoteSnapshot==null?Message("Waiting for host",SelectedHostLabel()):Compact());
                double height=CompactHeight();
                if(Height!=height)Height=height;
                return;
            }
            var offset=scroller?.Offset??default;
            if(layoutPage!=page)
            {
                bool collapsed=localService.Settings.SidebarCollapsed;
                var layout=new Grid{ColumnDefinitions=new ColumnDefinitions(collapsed?"60,*":"174,*")};var nav=new StackPanel{Spacing=6,Margin=new Thickness(8,14)};
                var collapse=Btn(collapsed?"»":"«  Collapse menu");ToolTip.SetTip(collapse,collapsed?"Expand menu":"Collapse menu");Avalonia.Automation.AutomationProperties.SetName(collapse,collapsed?"Expand menu":"Collapse menu");collapse.Click+=(_,_)=>{localService.Settings.SidebarCollapsed=!collapsed;Paths.Save(localService.Settings);layoutPage=null;RenderBody();};nav.Children.Add(collapse);
                foreach(var item in new[]{"Overview","Hardware","Sensors","Models","Agents","Token","History","Alerts","Settings"})
                {
                    var contents=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};contents.Children.Add(NavIcon(item));if(!collapsed)contents.Children.Add(Text(item,13));
                    var b=new Button{Content=contents,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,12),Cursor=new Cursor(StandardCursorType.Hand),Background=Paint("#1c283b","#e4edf9")};
                    ToolTip.SetTip(b,item);Avalonia.Automation.AutomationProperties.SetName(b,item);if(item==page)b.Background=Selected;b.Click+=(_,_)=>{if(page==item)return;page=item;scroller=null;layoutPage=null;RenderBody();};nav.Children.Add(b);
                }
                if(!collapsed){var host=Text((fleet!=null?SelectedHostLabel():"This PC")+"\n"+(fleet!=null?"Central dashboard":service.HostPlatform)+" · v"+typeof(MainWindow).Assembly.GetName().Version?.ToString(3),11,"#7f93af");host.Margin=new Thickness(12,26,0,0);nav.Children.Add(host);}
                layout.Children.Add(new Border{Child=new ScrollViewer{Content=nav},BorderBrush=Line,BorderThickness=new Thickness(0,0,1,0)});
                var main=new Grid{RowDefinitions=new RowDefinitions("Auto,*"),Margin=page=="Overview"?new Thickness(22,8,22,8):new Thickness(26,20)};
                if(page is "Hardware" or "Sensors" or "Agents")
                {
                    var search=new TextBox{Text=Query,PlaceholderText=page=="Agents"?"Search app, process, PID, model, session or status…":page=="Hardware"?"Search hardware, device or specification…":"Search device, sensor, unit, source or process…",Margin=new Thickness(0,0,0,16)};
                    var searchPage=page;
                    Avalonia.Automation.AutomationProperties.SetName(search,"Search "+searchPage);search.TextChanged+=(_,_)=>{searches[searchPage]=search.Text??"";if(page!=searchPage)return;if(scroller!=null)scroller.Offset=default;RenderBody();};main.Children.Add(search);
                }
                scroller=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroller,1);main.Children.Add(scroller);Grid.SetColumn(main,1);layout.Children.Add(main);body.Content=layout;layoutPage=page;
            }
            bool overview=page=="Overview"&&(fleet==null||fleet.SelectedMonitor?.RemoteSnapshot!=null);
            if(overview!=(footerActions.Content!=null)||overview&&!Equals(footerActions.Tag,RequestedThemeVariant))
            {
                footerActions.Content=overview?OverviewFooterActions():null;
                footerActions.Tag=RequestedThemeVariant;
            }
            Reconciler.Show(scroller!,fleet!=null&&page!="Settings"&&fleet.SelectedHost==""?FleetOverview():fleet!=null&&page!="Settings"&&fleet.SelectedMonitor?.RemoteSnapshot==null?Message("Waiting for host",SelectedHostLabel()):page switch{"Overview"=>Overview(),"Hardware"=>HardwarePage(),"Sensors"=>SensorsPage(),"Models"=>ModelsPage(),"Agents"=>AgentsPage(),"Token"=>TokenPage(),"History"=>HistoryPage(),"Alerts"=>AlertsPage(),_=>SettingsPage()});scroller.Offset=offset;
        }
        catch(Microsoft.Data.Sqlite.SqliteException e)when(e.SqliteErrorCode is 11 or 26){ShowDatabaseError();}
        finally{rebuilding=false;}
    }
    void UpdateFooter(string status)
    {
        string sample=service.LastSample>0?DateTimeOffset.FromUnixTimeSeconds(service.LastSample).ToLocalTime().ToString("MMM d HH:mm:ss zzz"):"Waiting for first sample";
        footer.Text=$"{(service.Fresh?"Live":"Waiting / stale")} · {service.HostName} · {service.HostPlatform} · {sample} · {service.Settings.RetentionDays}-day history · {status}";
        ToolTip.SetTip(footer,footer.Text+" · "+service.CpuName);
    }
    void ShowDatabaseError()
    {
        var error=Message("Monitoring database needs recovery","Usage and history are unavailable. Close AIPULSE and restore a verified backup.");
        if(!localService.Settings.Compact&&scroller!=null)scroller.Content=error;
        else body.Content=error;
        footer.Text="Database integrity error";
    }
    Control NavIcon(string name)
    {
        string data=name switch{
            "Overview"=>"M2,2 H8 V8 H2 Z M12,2 H18 V8 H12 Z M2,12 H8 V18 H2 Z M12,12 H18 V18 H12 Z",
            "Hardware"=>"M5,5 H15 V15 H5 Z M8,1 V5 M12,1 V5 M8,15 V19 M12,15 V19 M1,8 H5 M1,12 H5 M15,8 H19 M15,12 H19",
            "Sensors"=>"M1,10 H5 L8,3 L12,17 L15,10 H19",
            "Models"=>"M10,1 L19,6 L10,11 L1,6 Z M1,10 L10,15 L19,10 M1,14 L10,19 L19,14",
            "Agents"=>"M4,6 H16 V16 H4 Z M10,2 V6 M7,10 H8 M12,10 H13 M7,13 H13 M1,9 V13 M19,9 V13",
            "Token"=>"M10,1 L18,5 V15 L10,19 L2,15 V5 Z M6,7 H14 M10,7 V14",
            "Assistant"=>"M2,3 H18 V14 H10 L5,18 V14 H2 Z M6,7 H14 M6,10 H11",
            "History"=>"M3,6 A8,8 0 1 1 2,12 M3,1 V6 H8 M10,5 V10 L14,12",
            "Alerts"=>"M10,2 L19,18 H1 Z M10,7 V12 M10,14 V16",
            _=>"M10,1 L13,4 L17,4 L17,8 L19,10 L17,13 L17,17 L13,17 L10,19 L7,17 L3,17 L3,13 L1,10 L3,7 L3,3 L7,3 Z M10,7 A3,3 0 1 0 10,13 A3,3 0 1 0 10,7"};
        return new Avalonia.Controls.Shapes.Path{Data=Geometry.Parse(data),Stroke=Ink("#b8c7df"),StrokeThickness=1.4,Width=20,Height=20,Stretch=Stretch.Uniform};
    }
    public void Capture()
    {
        try{var file=Path.Combine(Paths.Root,localService.Settings.Compact?"compact-live.png":"dashboard-live.png");var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96));bitmap.Render(this);bitmap.Save(file);}catch(Exception e){Paths.Log("capture",e);}
    }
}
