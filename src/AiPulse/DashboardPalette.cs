using Avalonia.Media;
using Avalonia.Styling;

namespace AiPulse;

public sealed partial class MainWindow
{
    bool LightTheme => localService.Settings.Compact
        ? localService.Settings.CompactLightTheme
        : localService.Settings.DashboardLightTheme;

    static readonly IBrush DarkCanvas=Brush.Parse("#0b111c"),LightCanvas=Brush.Parse("#edf2fa");
    IBrush Canvas => LightTheme?LightCanvas:DarkCanvas;
    IBrush Panel => Paint("#121c2b","#ffffff");
    IBrush Line => Paint("#26344a","#ccd8e8");
    IBrush Selected => Paint("#443477","#e3d9ff");
    IBrush Paint(string dark,string light)=>Brush.Parse(LightTheme?light:dark);

    void ApplyPalette()
    {
        RequestedThemeVariant=LightTheme?ThemeVariant.Light:ThemeVariant.Dark;
        Foreground=Ink(null);
        footer.Foreground=Ink("#91a4be");
        footerBar.BorderBrush=Line;
        ApplyGlassChrome(localService.Settings.Compact);
        StyleChatLauncher();
    }

    void TogglePalette(bool compact)
    {
        var settings=localService.Settings;
        if(compact)settings.CompactLightTheme=!settings.CompactLightTheme;
        else settings.DashboardLightTheme=!settings.DashboardLightTheme;
        Paths.Save(settings);
        ApplyPalette();
        BuildHeader();
        layoutPage=null;
        RenderBody();
    }

    IBrush Ink(string? dark)=>Brush.Parse(ThemeInk(dark,LightTheme));

    internal static string ThemeInk(string? dark,bool light)
    {
        if(!light)return dark??"#e1e9f7";
        return dark switch
        {
            null or "#e1e9f7" or "#e9f0ff" or "#f0f4ff" or "#dbe5f4" => "#14243a",
            "#59dfae" or "#45d6a4" => "#08694f",
            "#ff7187" => "#ae263f",
            "#e8b65f" or "#ffc58a" or "#f0b975" => "#865010",
            "#40d5ed" or "#55cee5" or "#72d4e8" => "#076783",
            "#ad8bfa" or "#bba3ff" => "#623db3",
            "#f2ac63" => "#985015",
            _ => "#435974"
        };
    }
}
