using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiPulse;
using Xunit;

public sealed class DriveTests
{
    static DriveItem Folder(string id, string name, string parent, bool canTrash = true) => new(id, name, true, 0, parent == "" ? [] : [parent], default, canTrash);
    static DriveItem File(string id, string name, string parent, long bytes) => new(id, name, false, bytes, parent == "" ? [] : [parent], default, true);

    [Fact]
    public void BuildsTreeWithSizesLargestFirst()
    {
        var items = new List<DriveItem>
        {
            Folder("a", "Photos", "root"), Folder("b", "Datasets", "root"), Folder("c", "raw", "b"),
            File("1", "p.jpg", "a", 100), File("2", "d.parquet", "c", 5000), File("3", "notes.txt", "root", 10),
        };
        var tree = DriveTree.Build(items, "root");
        Assert.Equal(5110, tree.Size);
        Assert.Equal("Datasets", tree.Children[0].Name);
        Assert.Equal("c", tree.Children[0].Children[0].RemoteId);
        Assert.Equal("My Drive/Datasets/raw", tree.Children[0].Children[0].FullPath);
        Assert.Equal(10, tree.OwnFileBytes);
        Assert.False(tree.CanDelete);
        Assert.Equal(3, tree.Files);
    }

    [Fact]
    public void ItemsOutsideMyDriveGoToOneBucketThatCannotBeDeleted()
    {
        var items = new List<DriveItem> { File("1", "shared.bin", "unknown-folder", 700), File("2", "orphan.bin", "", 300), Folder("x", "Shared folder", "elsewhere") };
        var tree = DriveTree.Build(items, "root");
        var bucket = Assert.Single(tree.Children);
        Assert.Equal(DriveTree.Elsewhere, bucket.Name);
        Assert.False(bucket.CanDelete);
        Assert.Equal(1000, bucket.Size);
        Assert.Equal("Shared folder", Assert.Single(bucket.Children).Name);
    }

    [Fact]
    public void FolderLoopDoesNotHangOrDuplicate()
    {
        var items = new List<DriveItem> { Folder("a", "A", "b"), Folder("b", "B", "a"), File("1", "f", "a", 50) };
        var tree = DriveTree.Build(items, "root");
        Assert.Equal(50, tree.Size);
        Assert.Equal(3, tree.Folders); // the loop lands in the "Not in My Drive" bucket, once each
        Assert.Equal(DriveTree.Elsewhere, Assert.Single(tree.Children).Name);
    }

    [Fact]
    public void FoldersDriveCannotTrashAreMarked()
    {
        var tree = DriveTree.Build([Folder("a", "Locked", "root", canTrash: false)], "root");
        Assert.False(tree.Children[0].CanDelete);
    }

    [Fact]
    public void ParsesTheFilesListPreferringQuotaBytes()
    {
        var json = JsonDocument.Parse("""
            [{"id":"f1","name":"model.gguf","mimeType":"application/octet-stream","size":"900","quotaBytesUsed":"1000","parents":["root"],"modifiedTime":"2026-03-01T10:00:00.000Z","capabilities":{"canTrash":true}},
             {"id":"d1","name":"Docs","mimeType":"application/vnd.google-apps.folder","parents":["root"],"capabilities":{"canTrash":false}},
             {"id":"g1","name":"Sheet","mimeType":"application/vnd.google-apps.spreadsheet"}]
            """).RootElement;
        var items = DriveTree.Parse(json);
        Assert.Equal(1000, items[0].Bytes);
        Assert.True(items[0].CanTrash);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), items[0].Modified);
        Assert.True(items[1].Folder);
        Assert.False(items[1].CanTrash);
        Assert.Equal(0, items[2].Bytes);
        Assert.Empty(items[2].Parents);
        Assert.Equal("model.gguf", Assert.Single(DriveTree.LargestFiles(items)).Name);
    }

    [Fact]
    public void PkceChallengeIsTheHashOfTheVerifier()
    {
        var (verifier, challenge) = DriveAccount.Pkce();
        Assert.InRange(verifier.Length, 43, 128);
        string expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
        string url = DriveAccount.AuthUrl("id.apps.googleusercontent.com", "http://127.0.0.1:5000/", challenge, "s1");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2F", url);
        Assert.True(DriveAccount.Configured("id.apps.googleusercontent.com"));
        Assert.False(DriveAccount.Configured("not-a-client"));
    }

    [Fact]
    public void TokenStoreRoundTripsAndIsOwnerOnly()
    {
        string file = Path.Combine(Path.GetTempPath(), "aipulse-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            TokenStore.Save(file, "refresh-123");
            Assert.Equal("refresh-123", TokenStore.Load(file));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(file));
            else Assert.DoesNotContain("refresh-123", System.IO.File.ReadAllText(file));
        }
        finally { System.IO.File.Delete(file); }
    }
}
