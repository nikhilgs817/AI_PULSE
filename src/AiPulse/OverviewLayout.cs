using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiPulse;

public record OverviewTileSize(int Width,int Height);

public sealed partial class MainWindow
{
    sealed record OverviewTile(string Id,string Label,int Span,Func<Control> Build,bool Optional=false);
    bool editingOverview;
    bool editingHistory;
    bool overviewDragPending;
    bool overviewShelfOpen;
    static readonly object LayoutHandle=new();
    sealed record LayoutSnapshot(List<string> Order,List<string> Hidden,Dictionary<string,OverviewTileSize> Sizes);
    readonly Stack<LayoutSnapshot> overviewUndo=new();
    readonly Stack<LayoutSnapshot> historyUndo=new();

    internal static List<string> TileOrder(IEnumerable<string> saved,IEnumerable<string> available)
    {
        var defaults=available.ToArray();
        var allowed=defaults.ToHashSet(StringComparer.Ordinal);
        return saved.Where(allowed.Contains).Concat(defaults).Distinct(StringComparer.Ordinal).ToList();
    }

    Control WidgetLayout(IReadOnlyList<OverviewTile> tiles,bool history=false,Control? summary=null)
    {
        var prefs=localService.Settings;
        var savedOrder=(history?prefs.HistoryGraphOrder:prefs.OverviewTileOrder)??[];
        var savedHidden=(history?prefs.HiddenHistoryGraphs:prefs.HiddenOverviewTiles)??[];
        var savedSizes=(history?prefs.HistoryGraphSizes:prefs.OverviewTileSizes)??[];
        var undoStack=history?historyUndo:overviewUndo;
        bool editing=history?editingHistory:editingOverview;
        var order=TileOrder(savedOrder,tiles.Select(t=>t.Id));
        var hidden=savedHidden.ToHashSet(StringComparer.Ordinal);
        var saved=savedOrder.ToHashSet(StringComparer.Ordinal);
        hidden.UnionWith(tiles.Where(t=>t.Optional&&!saved.Contains(t.Id)).Select(t=>t.Id));
        var visible=order.Where(id=>!hidden.Contains(id)).ToArray();
        var byId=tiles.ToDictionary(t=>t.Id);
        var root=new StackPanel{Spacing=4};
        var heading=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8};
        heading.Children.Add(history
            ? Heading("History",$"7-day detailed readings · {service.Settings.RetentionDays}-day minute summaries and usage records")
            : Text("Overview",22,"#f0f4ff",true));
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        Grid.SetColumn(tools,1);heading.Children.Add(tools);
        if(!editing)
        {
            if(history)tools.Children.Add(ChartRangePicker("History chart range",historySeconds,seconds=>historySeconds=seconds));
            else tools.Children.Add(ChartRangePicker("Overview chart range",overviewSeconds,seconds=>overviewSeconds=seconds));
        }
        Button Action(string label,Action action,string? name=null)
        {
            var button=Btn(label);
            AutomationProperties.SetName(button,name??label);
            button.Click+=(_,_)=>action();
            return button;
        }
        void Save(bool remember=true,Dictionary<string,OverviewTileSize>? sizes=null)
        {
            if(remember)undoStack.Push(new([..savedOrder],[..savedHidden],new(savedSizes)));
            if(history)
            {
                prefs.HistoryGraphOrder=order;
                prefs.HiddenHistoryGraphs=hidden.ToList();
                if(sizes!=null)prefs.HistoryGraphSizes=sizes;
            }
            else
            {
                prefs.OverviewTileOrder=order;
                prefs.HiddenOverviewTiles=hidden.ToList();
                if(sizes!=null)prefs.OverviewTileSizes=sizes;
            }
            Paths.Save(prefs);
            RenderBody();
        }
        void Undo()
        {
            if(!undoStack.TryPop(out var previous))return;
            order=TileOrder(previous.Order,tiles.Select(t=>t.Id));
            hidden=previous.Hidden.ToHashSet(StringComparer.Ordinal);
            var previousIds=previous.Order.ToHashSet(StringComparer.Ordinal);
            hidden.UnionWith(tiles.Where(t=>t.Optional&&!previousIds.Contains(t.Id)).Select(t=>t.Id));
            Save(remember:false,sizes:previous.Sizes);
        }
        static void MoveIn(List<string> list,string id,string destination)
        {
            if(id==destination)return;
            int index=list.IndexOf(destination);
            list.Remove(id);
            list.Insert(index,id);
        }
        void Move(string id,string destination)
        {
            MoveIn(order,id,destination);
            Save();
        }
        var edit=Action(editing?"Done editing":"Edit layout",()=>
        {
            if(history)editingHistory=!editingHistory;
            else editingOverview=!editingOverview;
            RenderBody();
        });
        edit.Background=Paint("#254778","#dceaff");
        edit.FontSize=11;edit.Padding=new Thickness(10,4);edit.MinHeight=26;
        var shelf=new StackPanel{Spacing=6};
        if(editing||history)
        {
            shelf.Children.Add(Text("Widget shelf",15,null,true));
            if(editing)shelf.Children.Add(Text("Drag a header to move a widget or its corner to resize it.",11,"#8ba0bd"));
            var flyout=new Flyout{Content=new ScrollViewer{Content=shelf,MaxHeight=340,Width=history?300:220},Placement=PlacementMode.BottomEdgeAlignedRight};
            var choices=new StackPanel{Spacing=6};
            var empty=Text("All widgets are on the grid.",11,"#8ba0bd");
            if(history)
            {
                var search=new TextBox{PlaceholderText="Find a graph"};
                AutomationProperties.SetName(search,"Find a history graph");
                search.TextChanged+=(_,_)=>ApplyFilter(search.Text??"");
                shelf.Children.Add(search);
            }
            foreach(var tile in tiles.Where(t=>hidden.Contains(t.Id)))
            {
                var add=Action("+ "+tile.Label,()=>{flyout.Hide();hidden.Remove(tile.Id);Save();},"Add "+tile.Label);
                var symbol=new Border{Child=Text("+",15,"#72d4e8",true),Background=Paint("#223854","#e2edff"),
                    CornerRadius=new CornerRadius(7),Width=24,Height=24,Padding=new Thickness(7,0)};
                var label=new Grid{ColumnDefinitions=new ColumnDefinitions("30,*"),Children={symbol}};
                var name=Text(tile.Label,12,null,true);
                Grid.SetColumn(name,1);label.Children.Add(name);
                add.Content=label;
                add.Background=Panel;
                add.Padding=new Thickness(7);
                add.HorizontalAlignment=HorizontalAlignment.Stretch;
                add.HorizontalContentAlignment=HorizontalAlignment.Left;
                add.Tag=tile.Label;
                choices.Children.Add(add);
            }
            shelf.Children.Add(choices);
            shelf.Children.Add(empty);
            ApplyFilter("");
            void ApplyFilter(string query)
            {
                foreach(var choice in choices.Children)choice.IsVisible=(choice.Tag as string)?.Contains(query.Trim(),StringComparison.OrdinalIgnoreCase)==true;
                empty.IsVisible=choices.Children.All(choice=>!choice.IsVisible);
                empty.Text=choices.Children.Count==0?"All widgets are on the grid.":"No matching graphs.";
            }
            if(editing)
            {
                var undo=Action("Undo",Undo);undo.IsEnabled=undoStack.Count>0;
                undo.FontSize=11;undo.Padding=new Thickness(10,4);undo.MinHeight=26;
                tools.Children.Add(undo);
                var reset=Action("Reset layout",()=>{order=tiles.Select(t=>t.Id).ToList();hidden=tiles.Where(t=>t.Optional).Select(t=>t.Id).ToHashSet();Save(sizes:[]);});
                reset.FontSize=11;reset.Padding=new Thickness(10,4);reset.MinHeight=26;
                tools.Children.Add(reset);
            }
            string shelfLabel=history&&!editing?"Add graph":"Widget shelf";
            var shelfButton=Btn(shelfLabel);
            shelfButton.FontSize=11;shelfButton.Padding=new Thickness(10,4);shelfButton.MinHeight=26;
            AutomationProperties.SetName(shelfButton,shelfLabel);
            flyout.Opened+=(_,_)=>overviewShelfOpen=true;
            flyout.Closed+=(_,_)=>overviewShelfOpen=false;
            shelfButton.Flyout=flyout;
            tools.Children.Add(shelfButton);
        }
        tools.Children.Add(edit);
        root.Children.Add(heading);
        if(summary is not null)root.Children.Add(summary);
        double workspace=(Bounds.Width>0?Bounds.Width:Width)-(prefs.SidebarCollapsed?60:174)-52;
        int columns=workspace>=840?4:workspace>=620?3:2;
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions(string.Join(",",Enumerable.Repeat("*",columns)))};
        var cells=new Dictionary<string,Border>();
        var spans=new Dictionary<string,int>();
        void PositionTiles(IEnumerable<string> ids)
        {
            int r=0,c=0;
            foreach(var id in ids)
            {
                int span=spans[id];
                if(c+span>columns){r++;c=0;}
                while(grid.RowDefinitions.Count<=r)grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(cells[id],r);Grid.SetColumn(cells[id],c);Grid.SetColumnSpan(cells[id],span);
                c+=span;
            }
        }
        void Preview(string source,string? target)
        {
            var preview=visible.ToList();
            if(target!=null)MoveIn(preview,source,target);
            PositionTiles(preview);
        }
        for(int i=0;i<visible.Length;i++)
        {
            var tile=byId[visible[i]];
            var size=savedSizes.GetValueOrDefault(tile.Id,new(tile.Span,0))??new(tile.Span,0);
            int span=Math.Clamp(size.Width,tile.Span==1?1:Math.Min(2,columns),columns);
            int height=Math.Clamp(size.Height,0,8);
            var panel=new DockPanel();
            var content=tile.Build();
            if(editing)
            {
                var bar=new DockPanel{Margin=new Thickness(0,0,0,6)};
                var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=1};
                void TileAction(string glyph,Action action,string name)
                {
                    var button=Action(glyph,action,name);
                    button.Background=Brushes.Transparent;
                    button.Padding=new Thickness(4);
                    button.MinWidth=22;
                    ToolTip.SetTip(button,name);
                    buttons.Children.Add(button);
                }
                if(i>0){string previous=visible[i-1];TileAction("←",()=>Move(tile.Id,previous),"Move "+tile.Label+" earlier");}
                if(i+1<visible.Length){string next=visible[i+1];TileAction("→",()=>Move(tile.Id,next),"Move "+tile.Label+" later");}
                TileAction("×",()=>{hidden.Add(tile.Id);Save();},"Remove "+tile.Label);
                DockPanel.SetDock(buttons,Dock.Right);
                bar.Children.Add(buttons);
                var headingRow=new Grid{ColumnDefinitions=new ColumnDefinitions("15,*"),Children={Text("⠿",12,"#8ba0bd")}};
                var tileName=Text(tile.Label,11,null,true);
                Grid.SetColumn(tileName,1);headingRow.Children.Add(tileName);
                var handle=new Border{Child=headingRow,Background=Brushes.Transparent,
                    Padding=new Thickness(0,4),Cursor=new Cursor(StandardCursorType.SizeAll),Tag=LayoutHandle};
                headingRow.IsHitTestVisible=false;
                AutomationProperties.SetName(handle,"Drag "+tile.Label);
                bar.Children.Add(handle);
                DockPanel.SetDock(bar,Dock.Top);
                panel.Children.Add(bar);
                WireTileDrag(handle,tile.Id,grid,cells,Move,Preview);
                if(content is Border {Child:Control body} card)
                {
                    card.Child=null;
                    if(body is StackPanel {Children.Count:>0} stack && stack.Children[0] is TextBlock cardHeading &&
                        string.Equals(cardHeading.Text,tile.Label,StringComparison.OrdinalIgnoreCase))
                        stack.Children.RemoveAt(0);
                    content=body;
                }
            }
            panel.Children.Add(content);
            var cell=new Border{Margin=new Thickness(4),BorderThickness=new Thickness(2),BorderBrush=Brushes.Transparent,CornerRadius=new CornerRadius(12),
                MinHeight=height*48};
            if(editing)
            {
                cell.Background=Panel;cell.BorderBrush=Line;cell.BorderThickness=new Thickness(1);
                panel.Margin=new Thickness(11,8,11,14);
                var grip=new Border{Child=Text("◢",12,"#8ba0bd"),Width=24,Height=24,Padding=new Thickness(4),
                    Background=Brushes.Transparent,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Tag=LayoutHandle,
                    Cursor=new Cursor(StandardCursorType.BottomRightCorner)};
                AutomationProperties.SetName(grip,"Resize "+tile.Label);
                var overlay=new Grid{Children={panel,grip}};
                cell.Child=overlay;
                WireResize(grip,cell,grid,columns,span,tile.Span==1?1:2,(width,rows)=>
                {
                    Save(sizes:new(savedSizes){[tile.Id]=new(width,rows)});
                });
            }
            else cell.Child=panel;
            cells.Add(tile.Id,cell);
            spans.Add(tile.Id,span);
            grid.Children.Add(cell);
        }
        PositionTiles(visible);
        if(editing)
        {
            for(int r=0;r<grid.RowDefinitions.Count;r++)
                for(int c=0;c<columns;c++)
                {
                    var slot=new Border{Background=Paint("#17263b","#dce8fa"),CornerRadius=new CornerRadius(9),
                        Margin=new Thickness(4),MinHeight=48,IsHitTestVisible=false,ZIndex=-1};
                    Grid.SetRow(slot,r);Grid.SetColumn(slot,c);grid.Children.Add(slot);
                }
        }
        if(visible.Length==0)grid.Children.Add(Message(history?"No graphs displayed":"No tiles displayed",editing?"Add from the widget shelf.":"Open Edit layout to add widgets."));
        root.Children.Add(grid);
        return root;
    }

    void WireResize(Border grip,Border cell,Grid grid,int columns,int width,int minimum,Action<int,int> save)
    {
        IPointer? pointer=null;
        Point origin=default;
        double height=0;
        double originalHeight=0;
        int desiredWidth=width,desiredRows=0;
        IBrush originalBorder=Brushes.Transparent;
        grip.PointerPressed+=(_,e)=>
        {
            if(overviewDragPending||!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)return;
            pointer=e.Pointer;origin=e.GetPosition(grid);height=cell.Bounds.Height;
            originalHeight=cell.MinHeight;
            desiredRows=Math.Clamp((int)Math.Ceiling(height/48),0,8);
            originalBorder=cell.BorderBrush??Brushes.Transparent;
            cell.BorderBrush=Paint("#ad8bfa","#623db3");
            overviewDragPending=true;pointer.Capture(grip);e.Handled=true;
        };
        grip.PointerMoved+=(_,e)=>
        {
            if(e.Pointer!=pointer)return;
            var delta=e.GetPosition(grid)-origin;
            desiredWidth=Math.Clamp(width+(int)Math.Round(delta.X/Math.Max(1,grid.Bounds.Width/columns)),minimum,columns);
            desiredRows=Math.Clamp((int)Math.Round((height+delta.Y)/48),0,8);
            cell.MinHeight=desiredRows*48;
            ToolTip.SetTip(grip,$"{desiredWidth} columns · {desiredRows} height steps");
            e.Handled=true;
        };
        grip.PointerReleased+=(_,e)=>
        {
            if(e.Pointer!=pointer)return;
            pointer=null;overviewDragPending=false;cell.BorderBrush=originalBorder;e.Pointer.Capture(null);e.Handled=true;
            if(Dragged(origin,e.GetPosition(grid)))save(desiredWidth,desiredRows);
            else cell.MinHeight=originalHeight;
        };
        grip.PointerCaptureLost+=(_,_)=>{if(pointer==null)return;pointer=null;overviewDragPending=false;cell.BorderBrush=originalBorder;RenderBody();};
    }

    void WireTileDrag(Border handle,string id,Grid grid,Dictionary<string,Border> cells,Action<string,string> move,Action<string,string?> preview)
    {
        IPointer? pointer=null;
        string? target=null;
        Point origin=default;
        Dictionary<string,Rect> targets=[];
        void Clear()
        {
            overviewDragPending=false;
            pointer=null;
            cells[id].BorderBrush=Line;
            target=null;
            preview(id,null);
        }
        handle.PointerPressed+=(_,e)=>
        {
            if(overviewDragPending||!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)return;
            pointer=e.Pointer;
            origin=e.GetPosition(grid);
            targets=cells.Where(pair=>pair.Key!=id).ToDictionary(pair=>pair.Key,
                pair=>new Rect(pair.Value.TranslatePoint(default,grid)??default,pair.Value.Bounds.Size));
            overviewDragPending=true;
            pointer.Capture(handle);
            e.Handled=true;
        };
        handle.PointerMoved+=(_,e)=>
        {
            if(e.Pointer!=pointer)return;
            var point=e.GetPosition(grid);
            var next=!Dragged(origin,point)?null:targets.FirstOrDefault(pair=>pair.Value.Contains(point)).Key;
            if(next==target)return;
            target=next;
            preview(id,target);
            if(target!=null)cells[id].BorderBrush=Paint("#ad8bfa","#623db3");
            else cells[id].BorderBrush=Line;
            e.Handled=true;
        };
        handle.PointerReleased+=(_,e)=>
        {
            if(e.Pointer!=pointer)return;
            var point=e.GetPosition(grid);
            var destination=Dragged(origin,point)?targets.FirstOrDefault(pair=>pair.Value.Contains(point)).Key:null;
            Clear();
            e.Pointer.Capture(null);
            e.Handled=true;
            if(destination!=null)move(id,destination);
        };
        handle.PointerCaptureLost+=(_,_)=>{if(pointer!=null)Clear();};
    }

    static bool Dragged(Point origin,Point point)=>Math.Abs(point.X-origin.X)>=5||Math.Abs(point.Y-origin.Y)>=5;
}
