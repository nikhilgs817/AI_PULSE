using AiPulse;
using Xunit;

public sealed class DiskTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "aipulse-disk-" + Guid.NewGuid().ToString("N"));

    public DiskTests()
    {
        Write("models/huggingface/hub/a.bin", 3000);
        Write("models/huggingface/hub/b.bin", 2000);
        Write("web/node_modules/x.js", 500);
        Write("notes.txt", 100);
    }

    void Write(string relative, int bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public void ScanAddsUpSizesAndSortsLargestFirst()
    {
        var progress = new DiskScanProgress();
        var tree = DiskScanner.Scan(root, default, progress);
        Assert.Equal(5600, tree.Size);
        Assert.Equal(4, tree.Files);
        Assert.Equal(100, tree.OwnFileBytes);
        Assert.Equal(["models", "web"], tree.Children.Select(c => c.Name));
        Assert.Equal(4, progress.FilesSeen);
        Assert.Equal(5600, progress.BytesSeen);
    }

    [Fact]
    public void ScanDoesNotFollowLinks()
    {
        var link = Path.Combine(root, "loop");
        try { Directory.CreateSymbolicLink(link, root); } catch (Exception) { return; } // Windows without the right to make links.
        var tree = DiskScanner.Scan(root);
        Assert.Equal(5600, tree.Size);
        Assert.DoesNotContain(tree.Children, c => c.Name == "loop");
    }

    [Fact]
    public void LabelsAiAndDevFolders()
    {
        var tree = DiskScanner.Scan(root);
        var labelled = DiskScanner.Labelled(tree);
        Assert.Equal(["Hugging Face cache", "Node packages"], labelled.Select(n => n.Label));
        Assert.Equal("Ollama models", DiskLabels.For(@"C:\Users\me\.ollama\models"));
        Assert.Equal("Conda package cache", DiskLabels.For("/home/me/miniconda3/pkgs/"));
        Assert.Null(DiskLabels.For("/home/me/projects"));
    }

    [Fact]
    public void RemoveSubtractsFromEveryAncestor()
    {
        var tree = DiskScanner.Scan(root);
        var models = tree.Children.Single(c => c.Name == "models");
        var hub = models.Children.Single().Children.Single();
        long folders = tree.Folders;
        DiskScanner.Remove(hub);
        Assert.Equal(600, tree.Size);
        Assert.Equal(2, tree.Files);
        Assert.Equal(0, models.Size);
        Assert.Equal(folders - 1, tree.Folders);
        Assert.Equal("web", tree.Children[0].Name);
    }

    [Fact]
    public void SafetyBlocksRootsSystemTreesAndKeptFolders()
    {
        string sys = Path.Combine(root, "sys"), home = Path.Combine(root, "home");
        string[] keep = [home], system = [sys];
        Assert.NotNull(DiskSafety.Blocked(Path.GetPathRoot(root)!, keep, system));
        Assert.NotNull(DiskSafety.Blocked(sys, keep, system));
        Assert.NotNull(DiskSafety.Blocked(Path.Combine(sys, "drivers"), keep, system));
        Assert.NotNull(DiskSafety.Blocked(root, keep, system)); // holds the system tree
        Assert.NotNull(DiskSafety.Blocked(home, keep, system));
        Assert.Null(DiskSafety.Blocked(Path.Combine(home, ".cache", "pip"), keep, system));
        Assert.NotNull(DiskSafety.Blocked(Path.Combine(root, "$Recycle.Bin"), keep, system));
    }
}
