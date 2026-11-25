using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace AiPulse;

/// <summary>
/// The app's motion, kept small and purposeful: a page settles in when you switch to it, buttons give a press,
/// bars glide to new values, and the footer's dot pulses while readings are live. "Reduce motion" turns it all off.
/// </summary>
public static class Motion
{
    public static bool Reduced { get; set; }

    static readonly Animation PageEnter = new()
    {
        Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut(), FillMode = FillMode.None,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 10d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
        },
    };

    static readonly Animation Breathe = new()
    {
        Duration = TimeSpan.FromMilliseconds(1600), IterationCount = IterationCount.Infinite, Easing = new SineEaseInOut(), PlaybackDirection = PlaybackDirection.Alternate,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0.3d) } },
        },
    };

    /// <summary>Fades and lifts a freshly shown page into place.</summary>
    public static void Enter(Visual target)
    {
        if (Reduced) return;
        if (target.RenderTransform is not TranslateTransform) target.RenderTransform = new TranslateTransform();
        _ = PageEnter.RunAsync(target);
    }

    static CancellationTokenSource? pulse;

    /// <summary>Pulses the live dot while readings are fresh; holds it still otherwise.</summary>
    public static void Pulse(Ellipse dot, bool live)
    {
        bool running = pulse != null;
        if (live && !Reduced && !running) { pulse = new(); _ = Breathe.RunAsync(dot, pulse.Token); }
        else if ((!live || Reduced) && running) { pulse!.Cancel(); pulse = null; dot.Opacity = 1; }
    }

    /// <summary>App-wide styles: a quick press on buttons and bars that glide to new values.</summary>
    public static void AddStyles(Styles styles)
    {
        if (Reduced) return;
        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(Animatable.TransitionsProperty, new Transitions
                {
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(90), Easing = new CubicEaseOut() },
                }),
            },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(":pressed")) { Setters = { new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("scale(0.97)")) } });
        styles.Add(new Style(x => x.OfType<ProgressBar>())
        {
            Setters =
            {
                new Setter(Animatable.TransitionsProperty, new Transitions
                {
                    new DoubleTransition { Property = Avalonia.Controls.Primitives.RangeBase.ValueProperty, Duration = TimeSpan.FromMilliseconds(450), Easing = new CubicEaseOut() },
                }),
            },
        });
    }
}
