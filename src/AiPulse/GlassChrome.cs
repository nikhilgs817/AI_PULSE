using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiPulse;

public sealed partial class MainWindow
{
    readonly Border glassTint=new(){IsHitTestVisible=false,CornerRadius=new CornerRadius(18)};
    readonly Border glassSheen=new(){IsHitTestVisible=false,CornerRadius=new CornerRadius(18)};
    readonly Border glassEdge=new(){IsHitTestVisible=false,CornerRadius=new CornerRadius(18),BorderThickness=new Thickness(1)};
    readonly Border glassInnerEdge=new(){IsHitTestVisible=false,Margin=new Thickness(1),CornerRadius=new CornerRadius(17),BorderThickness=new Thickness(1)};

    void InitializeGlassChrome()
    {
        // Use the live desktop blur, not an opaque gradient pretending to be glass.
        // Passive reflection layers leave the existing drag and control interactions intact.
        var surface=new Grid();surface.Children.Add(glassTint);surface.Children.Add(glassSheen);surface.Children.Add(shell);surface.Children.Add(glassEdge);surface.Children.Add(glassInnerEdge);Content=surface;
        TransparencyBackgroundFallback=Canvas;
    }

    void ApplyGlassChrome(bool compact)
    {
        Background=compact?Brushes.Transparent:Canvas;
        WindowDecorations=compact?WindowDecorations.None:WindowDecorations.BorderOnly;
        // Prefer clear glass over strongly tinted acrylic when the compositor lacks a plain blur.
        TransparencyLevelHint=compact?[WindowTransparencyLevel.Blur,WindowTransparencyLevel.Transparent,WindowTransparencyLevel.AcrylicBlur,WindowTransparencyLevel.None]:[WindowTransparencyLevel.None];
        glassTint.IsVisible=glassSheen.IsVisible=glassEdge.IsVisible=glassInnerEdge.IsVisible=compact;
        if(!compact)return;
        glassTint.Background=new LinearGradientBrush
        {
            StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,1,RelativeUnit.Relative),
            GradientStops=[new GradientStop(Color.Parse("#98121f2b"),0),new GradientStop(Color.Parse("#a0111923"),0.5),new GradientStop(Color.Parse("#981c1b2b"),1)]
        };
        glassSheen.Background=new LinearGradientBrush
        {
            StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(0,1,RelativeUnit.Relative),
            GradientStops=[new GradientStop(Color.Parse("#24ffffff"),0),new GradientStop(Color.Parse("#02ffffff"),0.23),new GradientStop(Color.Parse("#00ffffff"),0.7),new GradientStop(Color.Parse("#12d6e9ff"),1)]
        };
        glassEdge.BorderBrush=new LinearGradientBrush
        {
            StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,1,RelativeUnit.Relative),
            GradientStops=[new GradientStop(Color.Parse("#e8efffff"),0),new GradientStop(Color.Parse("#658fcbd5"),0.18),new GradientStop(Color.Parse("#18ffffff"),0.5),new GradientStop(Color.Parse("#658b8dc4"),0.82),new GradientStop(Color.Parse("#cce9edff"),1)]
        };
        glassInnerEdge.BorderBrush=new LinearGradientBrush
        {
            StartPoint=new RelativePoint(1,0,RelativeUnit.Relative),EndPoint=new RelativePoint(0,1,RelativeUnit.Relative),
            GradientStops=[new GradientStop(Color.Parse("#60ffffff"),0),new GradientStop(Color.Parse("#00ffffff"),0.4),new GradientStop(Color.Parse("#08ffffff"),0.7),new GradientStop(Color.Parse("#48ffffff"),1)]
        };
    }

    static void StyleGlassButton(Button button)
    {
        button.Background=Brush.Parse("#20ffffff");button.BorderBrush=Brush.Parse("#60e7f1ff");button.BorderThickness=new Thickness(1);button.CornerRadius=new CornerRadius(12);
    }
}
