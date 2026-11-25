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

// Small building blocks every page uses: text, buttons, cards, metrics, key/value pairs and tables.
public sealed partial class MainWindow
{
    static void OpenFolder(string folder){try{Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows()?"explorer.exe":"xdg-open"){ArgumentList={folder},UseShellExecute=true});}catch{}}
    TextBlock Text(string text,double size=13,string? color=null,bool bold=false)=>new(){Text=text,FontSize=size,Foreground=Ink(color),FontWeight=bold?FontWeight.SemiBold:FontWeight.Normal,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    Button Btn(string label)=>new(){Content=label,Cursor=new Cursor(StandardCursorType.Hand),Background=Paint("#1c283b","#e4edf9")};
    Control Heading(string title,string sub){var s=new StackPanel{Spacing=7};s.Children.Add(Text(title,27,"#f0f4ff",true));s.Children.Add(Text(sub,12,"#8ba0bd"));return s;}
    Border Card(Control child)=>new(){Child=child,Padding=new Thickness(18),Background=Panel,CornerRadius=new CornerRadius(10),BorderBrush=Line,BorderThickness=new Thickness(1)};
    Border Metric(string label,string value,string caption,string? color=null){var s=new StackPanel{Spacing=10};s.Children.Add(Text(label,15,"#e1e9f7",true));s.Children.Add(Text(value,29,color??"#e9f0ff",true));s.Children.Add(Text(caption,10,"#7f93af"));return Card(s);}
    Control Pair(string label,string value){var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};grid.Children.Add(Text(label,12,"#9eb1ca"));var v=Text(value,16,"#72d4e8",true);Grid.SetColumn(v,1);grid.Children.Add(v);return grid;}
    Control Message(string title,string detail){var s=new StackPanel{Spacing=6};s.Children.Add(Text(title,13,"#bba3ff",true));s.Children.Add(Text(detail,12,"#9eb1ca"));return Card(s);}
    Control Table(string[] headers,IEnumerable<string[]> data,string columns)
    {
        var stack=new StackPanel();int index=0;
        void Row(string[] values,bool header){var grid=new Grid{ColumnDefinitions=new ColumnDefinitions(columns),Margin=new Thickness(12,header?9:12)};for(int i=0;i<values.Length;i++){var text=Text(values[i],header?10:12,header?"#8298b7":"#dbe5f4",header);text.Margin=new Thickness(0,0,10,0);Grid.SetColumn(text,i);grid.Children.Add(text);}stack.Children.Add(new Border{Child=grid,Background=header?Paint("#182438","#e5edf9"):index++%2==0?Panel:Paint("#101927","#f6f8fc"),BorderBrush=Line,BorderThickness=new Thickness(0,0,0,1)});}
        Row(headers,true);foreach(var row in data)Row(row,false);return new Border{Child=stack,CornerRadius=new CornerRadius(7),ClipToBounds=true};
    }
}
