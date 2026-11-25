using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AiPulse.Tests;

// Runs each existing check suite as its own test and reports every failed check, not just the first.
// New tests should be plain [Fact]s; these wrappers keep the older suites running until they are split up.
public sealed class SuiteTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "aipulse-tests-" + Guid.NewGuid().ToString("N"));

    public SuiteTests()
    {
        Paths.Initialize(["--data-dir", root]);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void RunSuite(Action<string, Action<bool, string>> suite, string folder)
    {
        var failures = new List<string>();
        int passed = 0;
        suite(folder, (ok, name) => { if (ok) passed++; else failures.Add(name); });
        Assert.True(failures.Count == 0, $"{failures.Count} check(s) failed, {passed} passed:\n- " + string.Join("\n- ", failures));
        Assert.True(passed > 0, "The suite ran no checks.");
    }

    string Folder(string name) { var f = Path.Combine(root, name); Directory.CreateDirectory(f); return f; }

    [Fact] public void UsageFramework() => RunSuite(UsageFrameworkTests.Run, Folder("usage"));
    [Fact] public void SubscriptionQuotas() => RunSuite(SubscriptionQuotaTests.Run, Folder("quotas"));
    [Fact] public void CaptureReliability() => RunSuite(CaptureReliabilityTests.Run, Folder("capture"));
    [Fact] public void MonitoringFeatures() => RunSuite(MonitoringFeatureTests.Run, Folder("monitoring"));
    [Fact] public void Backups() => RunSuite(SelfTests.RunBackupChecks, Folder("backups"));
    [Fact] public void Core() => RunSuite(SelfTests.CoreChecks, Folder("core"));
}
