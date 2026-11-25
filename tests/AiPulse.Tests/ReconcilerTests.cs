using Avalonia.Controls;
using Avalonia.Media;
using AiPulse;
using Xunit;

public class ReconcilerTests
{
    static StackPanel Page(string value, string color, Action onClick, int rows)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = value, Foreground = Brush.Parse(color) });
        var button = new Button { Content = "Delete…" };
        button.Click += (_, _) => onClick();
        stack.Children.Add(button);
        for (int i = 0; i < rows; i++) stack.Children.Add(new Border { Child = new TextBlock { Text = "row " + i } });
        return stack;
    }

    [Fact]
    public void PatchesPlainVisualsInPlace()
    {
        var host = new ContentControl();
        Reconciler.Show(host, Page("10 GB", "#ff0000", () => { }, 2));
        var shown = (StackPanel)host.Content!;
        var label = (TextBlock)shown.Children[0];

        Reconciler.Show(host, Page("12 GB", "#00ff00", () => { }, 2));

        Assert.Same(shown, host.Content);
        Assert.Same(label, shown.Children[0]);
        Assert.Equal("12 GB", label.Text);
        Assert.Equal(Colors.Lime, ((ISolidColorBrush)label.Foreground!).Color);
    }

    [Fact]
    public void SwapsButtonsSoHandlersNeverGoStale()
    {
        var host = new ContentControl();
        string? deleted = null;
        Reconciler.Show(host, Page("a", "#fff", () => deleted = "old folder", 1));
        var firstButton = ((StackPanel)host.Content!).Children[1];

        Reconciler.Show(host, Page("a", "#fff", () => deleted = "new folder", 1));
        var button = (Button)((StackPanel)host.Content!).Children[1];

        Assert.NotSame(firstButton, button);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("new folder", deleted);
    }

    [Fact]
    public void AddsAndRemovesRowsAtTheEnd()
    {
        var host = new ContentControl();
        Reconciler.Show(host, Page("a", "#fff", () => { }, 1));
        Reconciler.Show(host, Page("a", "#fff", () => { }, 4));
        Assert.Equal(6, ((StackPanel)host.Content!).Children.Count);
        Reconciler.Show(host, Page("a", "#fff", () => { }, 0));
        Assert.Equal(2, ((StackPanel)host.Content!).Children.Count);
    }

    [Fact]
    public void ClearsPropertiesTheNewTreeNoLongerSets()
    {
        var host = new ContentControl();
        Reconciler.Show(host, new TextBlock { Text = "x", FontWeight = FontWeight.Bold });
        var shown = host.Content;
        Reconciler.Show(host, new TextBlock { Text = "x" });
        Assert.Same(shown, host.Content);
        Assert.Equal(FontWeight.Normal, ((TextBlock)host.Content!).FontWeight);
    }
}
