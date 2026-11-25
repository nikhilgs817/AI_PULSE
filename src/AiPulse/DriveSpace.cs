using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiPulse;

/// <summary>One file or folder in Google Drive, with only the fields the space view needs.</summary>
public sealed record DriveItem(string Id, string Name, bool Folder, long Bytes, string[] Parents, DateTime Modified, bool CanTrash);

/// <summary>Drive's storage quota. Limit is null for unlimited plans.</summary>
public sealed record DriveQuota(long? Limit, long Usage, long UsageInDrive, long UsageInTrash);

/// <summary>Turns a flat Drive listing into the same folder tree the Disk page draws.</summary>
public static class DriveTree
{
    public const string Elsewhere = "Not in My Drive";

    /// <summary>
    /// Builds the tree under My Drive. Items whose parent is not in the listing (shared drives, files
    /// shared with you that you own, orphans) go under one extra folder so their space is still counted.
    /// A file with several parents is counted once, under its first parent.
    /// </summary>
    public static DiskNode Build(IReadOnlyList<DriveItem> items, string rootId)
    {
        var root = new DiskNode { Name = "My Drive", FullPath = "My Drive", RemoteId = rootId, CanDelete = false };
        var folders = new Dictionary<string, DiskNode> { [rootId] = root };
        var folderItems = items.Where(i => i.Folder).ToDictionary(i => i.Id);
        DiskNode? elsewhere = null;
        DiskNode Elsewhere() => elsewhere ??= Attach(new DiskNode { Name = DriveTree.Elsewhere, FullPath = "My Drive/" + DriveTree.Elsewhere, Parent = root, CanDelete = false }, root);
        static DiskNode Attach(DiskNode node, DiskNode parent) { parent.Children.Add(node); return node; }

        DiskNode FolderNode(string id, HashSet<string> path)
        {
            if (folders.TryGetValue(id, out var known)) return known;
            var item = folderItems[id];
            DiskNode parent = item.Parents.Length > 0 && (folders.ContainsKey(item.Parents[0]) || folderItems.ContainsKey(item.Parents[0])) && path.Add(id)
                ? FolderNode(item.Parents[0], path) : Elsewhere();
            if (folders.TryGetValue(id, out known)) return known; // a parent loop already placed it
            var node = new DiskNode { Name = item.Name, FullPath = parent.FullPath + "/" + item.Name, Parent = parent, RemoteId = id, LastWriteUtc = item.Modified, CanDelete = item.CanTrash };
            folders[id] = node;
            return Attach(node, parent);
        }

        foreach (var folder in folderItems.Keys) FolderNode(folder, []);
        foreach (var file in items.Where(i => !i.Folder))
        {
            var parent = file.Parents.Length > 0 && folders.TryGetValue(file.Parents[0], out var p) ? p : Elsewhere();
            parent.OwnFileBytes += file.Bytes;
            parent.Files++;
        }
        Total(root);
        return root;
    }

    static void Total(DiskNode node)
    {
        foreach (var child in node.Children) Total(child);
        node.Size = node.OwnFileBytes + node.Children.Sum(c => c.Size);
        node.Files += node.Children.Sum(c => c.Files);
        node.Folders = node.Children.Count + node.Children.Sum(c => c.Folders);
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>The largest files anywhere in Drive.</summary>
    public static List<DriveItem> LargestFiles(IEnumerable<DriveItem> items, int limit = 25) =>
        items.Where(i => !i.Folder && i.Bytes > 0).OrderByDescending(i => i.Bytes).Take(limit).ToList();

    public static List<DriveItem> Parse(JsonElement files)
    {
        var list = new List<DriveItem>();
        foreach (var f in files.EnumerateArray())
        {
            string Str(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            long bytes = long.TryParse(Str("quotaBytesUsed"), out long q) ? q : long.TryParse(Str("size"), out long s) ? s : 0;
            string[] parents = f.TryGetProperty("parents", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray() : [];
            bool canTrash = f.TryGetProperty("capabilities", out var c) && c.TryGetProperty("canTrash", out var t) && t.ValueKind == JsonValueKind.True;
            DateTime modified = DateTime.TryParse(Str("modifiedTime"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var m) ? m : default;
            list.Add(new(Str("id"), Str("name"), Str("mimeType") == "application/vnd.google-apps.folder", bytes, parents, modified, canTrash));
        }
        return list;
    }
}

/// <summary>
/// Google sign-in for the Drive view: the installed-app flow with PKCE and a loopback redirect, as Google
/// documents for desktop apps. The person brings their own OAuth client (Google Cloud, "Desktop app").
/// Only the refresh token is kept, encrypted for the Windows user with DPAPI (owner-only file elsewhere).
/// </summary>
public sealed class DriveAccount(string clientId, string clientSecret, string tokenFile)
{
    public const string Scope = "https://www.googleapis.com/auth/drive";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    string? access;
    DateTime accessExpires;

    public bool Connected => File.Exists(tokenFile);
    public static bool Configured(string clientId) => clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal);

    public static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string AuthUrl(string clientId, string redirect, string challenge, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
        "&client_id=" + Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(redirect) +
        "&scope=" + Uri.EscapeDataString(Scope) + "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state;

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Opens Google's consent page in the browser and waits up to five minutes for the answer.</summary>
    public async Task Connect(Action<string> openBrowser, CancellationToken cancel)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string redirect = $"http://127.0.0.1:{port}/";
        using var http = new HttpListener();
        http.Prefixes.Add(redirect);
        http.Start();
        var (verifier, challenge) = Pkce();
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        openBrowser(AuthUrl(clientId, redirect, challenge, state));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string? code = null, error = null;
        while (code == null && error == null)
        {
            var context = await http.GetContextAsync().WaitAsync(timeout.Token);
            var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
            if (query["state"] != state) { context.Response.StatusCode = 400; context.Response.Close(); continue; }
            code = query["code"]; error = code == null ? query["error"] ?? "no code" : null;
            byte[] page = Encoding.UTF8.GetBytes(code != null ? "<p>AI Pulse is connected to Google Drive. You can close this tab.</p>" : "<p>Google Drive was not connected. You can close this tab.</p>");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(page, timeout.Token);
            context.Response.Close();
        }
        if (error != null) throw new InvalidOperationException("Google did not connect Drive: " + error);
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code!, ["client_id"] = clientId, ["client_secret"] = clientSecret, ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
        }), cancel);
        var json = await Json(response, cancel);
        string refresh = json.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
        if (refresh == "") throw new InvalidOperationException("Google did not return a refresh token.");
        TokenStore.Save(tokenFile, refresh);
        access = json.GetProperty("access_token").GetString();
        accessExpires = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 60);
    }

    /// <summary>Forgets the refresh token here and asks Google to revoke it.</summary>
    public async Task Disconnect()
    {
        string? refresh = TokenStore.Load(tokenFile);
        try { File.Delete(tokenFile); } catch (IOException) { }
        access = null;
        if (refresh != null)
            try { using var _ = await Http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh })); }
            catch (HttpRequestException) { }
    }

    async Task<string> Access(CancellationToken cancel)
    {
        if (access != null && DateTime.UtcNow < accessExpires) return access;
        string refresh = TokenStore.Load(tokenFile) ?? throw new InvalidOperationException("Connect Google Drive first.");
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = refresh, ["client_id"] = clientId, ["client_secret"] = clientSecret, ["grant_type"] = "refresh_token",
        }), cancel);
        var json = await Json(response, cancel);
        access = json.GetProperty("access_token").GetString();
        accessExpires = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 60);
        return access!;
    }

    async Task<JsonElement> Get(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Access(cancel));
        using var response = await Http.SendAsync(request, cancel);
        return await Json(response, cancel);
    }

    static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken cancel)
    {
        string body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
        {
            string detail = "";
            try { using var doc = JsonDocument.Parse(body); detail = doc.RootElement.TryGetProperty("error", out var e) ? (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()) ?? "" : ""; }
            catch (JsonException) { }
            throw new HttpRequestException($"Google answered {(int)response.StatusCode}. {detail}".Trim());
        }
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    public async Task<DriveQuota> Quota(CancellationToken cancel)
    {
        var q = (await Get("https://www.googleapis.com/drive/v3/about?fields=storageQuota", cancel)).GetProperty("storageQuota");
        long N(string name) => q.TryGetProperty(name, out var v) && long.TryParse(v.GetString(), out long n) ? n : 0;
        return new(q.TryGetProperty("limit", out _) ? N("limit") : null, N("usage"), N("usageInDrive"), N("usageInDriveTrash"));
    }

    public async Task<string> RootId(CancellationToken cancel) => (await Get("https://www.googleapis.com/drive/v3/files/root?fields=id", cancel)).GetProperty("id").GetString()!;

    /// <summary>Lists every file and folder you can see in Drive that is not in the trash, 1000 at a time.</summary>
    public async Task<List<DriveItem>> ListAll(Action<int> progress, CancellationToken cancel)
    {
        var all = new List<DriveItem>();
        string? page = null;
        do
        {
            string url = "https://www.googleapis.com/drive/v3/files?pageSize=1000&q=" + Uri.EscapeDataString("trashed=false and 'me' in owners") +
                "&fields=" + Uri.EscapeDataString("nextPageToken,files(id,name,mimeType,size,quotaBytesUsed,parents,modifiedTime,capabilities/canTrash)") +
                (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var json = await Get(url, cancel);
            all.AddRange(DriveTree.Parse(json.GetProperty("files")));
            progress(all.Count);
            page = json.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (page != null);
        return all;
    }

    /// <summary>Moves a file or folder to Drive's trash, where Drive keeps it for 30 days before deleting it.</summary>
    public async Task Trash(string id, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) + "?fields=id,trashed")
        { Content = new StringContent("{\"trashed\":true}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Access(cancel));
        using var response = await Http.SendAsync(request, cancel);
        var json = await Json(response, cancel);
        if (!(json.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True)) throw new InvalidOperationException("Drive did not confirm the move to trash.");
    }
}

/// <summary>Keeps the Drive refresh token: DPAPI for the current Windows user, or an owner-only file elsewhere.</summary>
public static class TokenStore
{
    public static void Save(string file, string token)
    {
        byte[] data = Encoding.UTF8.GetBytes(token);
        if (OperatingSystem.IsWindows()) data = Dpapi(data, protect: true);
        string temporary = file + ".tmp";
        File.WriteAllBytes(temporary, data);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, file, overwrite: true);
    }

    public static string? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            byte[] data = File.ReadAllBytes(file);
            if (OperatingSystem.IsWindows()) data = Dpapi(data, protect: false);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException) { Paths.Log("drive-token", e); return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    static byte[] Dpapi(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            const int UiForbidden = 0x1;
            bool ok = protect ? CryptProtectData(ref input, "AI Pulse Drive", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
