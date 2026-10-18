using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Media;
using Avalonia.Platform;

namespace AiPulse;
public sealed class App : Application
{
    public MonitorService? Monitor;
    public bool Quitting;
    public override void Initialize()
    {
        RequestedThemeVariant=ThemeVariant.Dark;Styles.Add(new FluentTheme());
        Styles.Add(new Style(x=>x.OfType<Button>()){Setters={new Setter(Button.CornerRadiusProperty,new CornerRadius(7)),new Setter(Button.BackgroundProperty,Brush.Parse("#1c283b")),new Setter(Button.BorderThicknessProperty,new Thickness(0)),new Setter(Button.PaddingProperty,new Thickness(12,8))}});
        Styles.Add(new Style(x=>x.OfType<TextBox>()){Setters={new Setter(TextBox.BackgroundProperty,Brush.Parse("#0b1320")),new Setter(TextBox.BorderBrushProperty,Brush.Parse("#2b3b50")),new Setter(TextBox.CornerRadiusProperty,new CornerRadius(6))}});
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
        {
            life.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var settings=Paths.Load();if(Program.Arguments.Contains("--expanded"))settings.Compact=false;
            Monitor=new(settings);var window=new MainWindow(Monitor);life.MainWindow=window;
            var menu=new NativeMenu();var show=new NativeMenuItem("Open AI Pulse");show.Click+=(_,_)=>{window.Show();window.Activate();};menu.Items.Add(show);
            var compact=new NativeMenuItem("Compact / expanded");compact.Click+=(_,_)=>{window.Show();window.ToggleMode();};menu.Items.Add(compact);
            var quit=new NativeMenuItem("Quit AI Pulse");quit.Click+=(_,_)=>{Quitting=true;life.Shutdown();};menu.Items.Add(quit);
            try{var tray=new TrayIcon{ToolTipText="AI Pulse · local AI monitoring",Menu=menu,Icon=new WindowIcon(AssetLoader.Open(new Uri("avares://AiPulse/Assets/pulse.ico")))};tray.Clicked+=(_,_)=>{window.Show();window.Activate();};TrayIcon.SetIcons(this,new TrayIcons{tray});}catch{}
            life.Exit+=(_,_)=>{Quitting=true;Monitor.Dispose();};Monitor.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
