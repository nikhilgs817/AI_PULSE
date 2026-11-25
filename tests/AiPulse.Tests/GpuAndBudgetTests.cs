using AiPulse;
using Xunit;

public class GpuAndBudgetTests
{
    [Fact]
    public void ParsesComputeProcesses()
    {
        var list = GpuDetails.ParseProcesses("""
            1234, /usr/bin/ollama, 5120
            99, C:\Program Files\Python\python.exe, [N/A]
            garbage line
            """);
        Assert.Equal(2, list.Count);
        Assert.Equal(new GpuProcess(1234, "ollama", 5120), list[0]);
        Assert.Equal("python.exe", list[1].Name);
        Assert.Null(list[1].MemoryMiB);
    }

    [Fact]
    public void ThrottleIgnoresIdleAndNamesRealReasons()
    {
        Assert.False(GpuDetails.ParseThrottle("0x0000000000000001")!.Active);
        var hot = GpuDetails.ParseThrottle("0x0000000000000044\n")!;
        Assert.True(hot.Active);
        Assert.Equal("power cap, thermal (hardware)", hot.Reasons);
        Assert.Null(GpuDetails.ParseThrottle("[Not Supported]"));
    }

    [Fact]
    public void BudgetsAreOffAtZero()
    {
        Assert.False(Budgets.Tokens(5_000_000, 0).Over);
        Assert.True(Budgets.Tokens(1_000_000, 1_000_000).Over);
        Assert.False(Budgets.Cost(2.5, 3).Over);
        Assert.True(Budgets.Cost(3.01, 3).Over);
        var day = Budgets.StartOfLocalDay(new DateTimeOffset(2026, 11, 25, 15, 30, 0, TimeSpan.FromHours(5.5)));
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeSeconds(), day);
    }
}
