using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace AiPulse;

/// <summary>
/// Updates a page that is already on screen to match a freshly built copy, instead of swapping the whole tree.
/// Pages are still written as plain "build the controls" functions. On every refresh the new tree is compared
/// with the old one: plain visuals (text, borders, panels, shapes, bars, images) are patched in place, so they
/// keep their layout, hover state and running animations. Anything interactive (buttons, inputs, expanders,
/// custom-drawn charts, anything with a cursor or a pointer handler) is swapped for the new instance, so an
/// event handler can never keep stale data captured from an earlier refresh.
/// </summary>
internal static class Reconciler
{
    static readonly HashSet<Type> Plain =
    [
        typeof(TextBlock), typeof(Border), typeof(StackPanel), typeof(Grid), typeof(WrapPanel), typeof(DockPanel),
        typeof(Canvas), typeof(UniformGrid), typeof(Panel), typeof(Avalonia.Controls.Shapes.Path), typeof(Rectangle), typeof(Ellipse),
        typeof(Line), typeof(ProgressBar), typeof(Image), typeof(Viewbox),
    ];

    static readonly AvaloniaProperty[] Attached =
    [
        Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, DockPanel.DockProperty,
        Canvas.LeftProperty, Canvas.TopProperty, Canvas.RightProperty, Canvas.BottomProperty, ToolTip.TipProperty,
        AutomationProperties.NameProperty, AutomationProperties.HelpTextProperty,
    ];

    static readonly Dictionary<Type, AvaloniaProperty[]> PropertyCache = [];

    /// <summary>Counts how many controls were patched and how many were swapped in the last call; used by tests and the bench.</summary>
    public static (int Patched, int Swapped) LastStats { get; private set; }

    static int patched, swapped;

    /// <summary>Shows <paramref name="next"/> in <paramref name="host"/>, reusing the controls already there where it can.</summary>
    public static void Show(ContentControl host, Control next)
    {
        patched = swapped = 0;
        if (host.Content is Control current && !ReferenceEquals(current, next))
        {
            var kept = Reconcile(current, next);
            if (!ReferenceEquals(kept, current)) host.Content = kept;
        }
        else host.Content = next;
        LastStats = (patched, swapped);
    }

    /// <summary>Returns the control that should be in the tree: <paramref name="current"/> updated in place, or <paramref name="next"/>.</summary>
    public static Control Reconcile(Control current, Control next)
    {
        // Keep a text box the person is typing in, so a refresh never steals focus or the caret.
        if (current is TextBox or NumericUpDown && current.GetType() == next.GetType() && current.IsKeyboardFocusWithin) { patched++; return current; }
        if (!CanPatch(current, next)) { swapped++; return next; }
        switch (current)
        {
            case Panel oldPanel:
                var newPanel = (Panel)next;
                var incoming = newPanel.Children.ToList();
                newPanel.Children.Clear();
                int shared = Math.Min(oldPanel.Children.Count, incoming.Count);
                for (int i = 0; i < shared; i++)
                {
                    var kept = Reconcile(oldPanel.Children[i], incoming[i]);
                    if (!ReferenceEquals(kept, oldPanel.Children[i])) oldPanel.Children[i] = kept;
                }
                if (oldPanel.Children.Count > incoming.Count) oldPanel.Children.RemoveRange(incoming.Count, oldPanel.Children.Count - incoming.Count);
                for (int i = shared; i < incoming.Count; i++) { oldPanel.Children.Add(incoming[i]); swapped++; }
                break;
            case Decorator oldDecorator:
                var newDecorator = (Decorator)next;
                var child = newDecorator.Child;
                newDecorator.Child = null;
                if (oldDecorator.Child is { } oldChild && child != null)
                {
                    var kept = Reconcile(oldChild, child);
                    if (!ReferenceEquals(kept, oldChild)) oldDecorator.Child = kept;
                }
                else if (!ReferenceEquals(oldDecorator.Child, child)) { oldDecorator.Child = child; swapped++; }
                break;
            case Viewbox oldBox:
                var newBox = (Viewbox)next;
                var boxChild = newBox.Child;
                newBox.Child = null;
                if (oldBox.Child is { } oldBoxChild && boxChild != null)
                {
                    var kept = Reconcile(oldBoxChild, boxChild);
                    if (!ReferenceEquals(kept, oldBoxChild)) oldBox.Child = kept;
                }
                else { oldBox.Child = boxChild; swapped++; }
                break;
        }
        CopyProperties(current, next);
        patched++;
        return current;
    }

    static bool CanPatch(Control current, Control next)
    {
        var type = next.GetType();
        if (current.GetType() != type || !Plain.Contains(type)) return false;
        if (current.IsSet(InputElement.CursorProperty) || next.IsSet(InputElement.CursorProperty)) return false;
        if (current is TextBlock { Inlines.Count: > 0 } || next is TextBlock { Inlines.Count: > 0 }) return false;
        if (!current.Classes.SequenceEqual(next.Classes)) return false;
        if (current is Grid oldGrid && next is Grid newGrid &&
            (Definitions(oldGrid.ColumnDefinitions.Select(d => d.Width)) != Definitions(newGrid.ColumnDefinitions.Select(d => d.Width)) ||
             Definitions(oldGrid.RowDefinitions.Select(d => d.Height)) != Definitions(newGrid.RowDefinitions.Select(d => d.Height))))
            return false;
        return true;
    }

    static string Definitions(IEnumerable<GridLength> lengths) => string.Join(",", lengths);

    static AvaloniaProperty[] PropertiesOf(Type type)
    {
        if (PropertyCache.TryGetValue(type, out var cached)) return cached;
        var list = AvaloniaPropertyRegistry.Instance.GetRegistered(type)
            .Where(p => !p.IsReadOnly && !p.IsDirect && !typeof(Control).IsAssignableFrom(p.PropertyType) && p.Name is not ("Inlines" or "Name"))
            .Concat(Attached).Distinct().ToArray();
        return PropertyCache[type] = list;
    }

    static void CopyProperties(Control current, Control next)
    {
        foreach (var property in PropertiesOf(current.GetType()))
        {
            bool nextSet = next.IsSet(property), currentSet = current.IsSet(property);
            if (!nextSet && !currentSet) continue;
            if (!nextSet) { current.ClearValue(property); continue; }
            var value = next.GetValue(property);
            if (!Same(current.GetValue(property), value)) current.SetValue(property, value);
        }
    }

    static bool Same(object? a, object? b)
    {
        if (Equals(a, b)) return true;
        if (a is ISolidColorBrush x && b is ISolidColorBrush y) return x.Color == y.Color && x.Opacity == y.Opacity && x.Transform == null && y.Transform == null;
        return false;
    }
}
