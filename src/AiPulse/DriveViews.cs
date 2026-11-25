using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AiPulse;

// The Drive page: which Google Drive folders and files use the most space, with delete to Drive's trash.
public sealed partial class MainWindow
{
    DriveAccount? driveAccount;
    DiskNode? driveRoot, driveCurrent;
    List<DriveItem> driveItems = [];
    DriveQuota? driveQuota;
    string driveBusy = "", driveNotice = "";
    int driveListed;
    CancellationTokenSource? driveWork;

    DriveAccount Drive => driveAccount ??= new(localService.Settings.DriveClientId, localService.Settings.DriveClientSecret, Path.Combine(Paths.Root, "drive-token.bin"));

    Control DrivePage()
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(Heading("Google Drive space", "Largest folders and files first · deletes go to Drive's trash after you confirm"));
        if (driveNotice != "") stack.Children.Add(Message("Drive", driveNotice));
        if (!DriveAccount.Configured(localService.Settings.DriveClientId)) { stack.Children.Add(DriveSetup()); return stack; }

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        if (driveWork != null)
        {
            var stop = Btn("Stop"); stop.Click += (_, _) => driveWork?.Cancel(); bar.Children.Add(stop);
            stack.Children.Add(bar);
            stack.Children.Add(Message(driveBusy, driveListed > 0 ? $"{Count(driveListed, "item")} listed so far." : "Waiting for Google…"));
            stack.Children.Add(Busy());
            return stack;
        }
        if (!Drive.Connected)
        {
            var connect = Btn("Connect Google Drive…"); connect.Click += (_, _) => DriveRun("Waiting for you to allow access in the browser", DriveConnect);
            bar.Children.Add(connect);
            var change = Btn("Change OAuth client"); change.Click += (_, _) => { localService.Settings.DriveClientId = ""; driveAccount = null; RenderBody(); };
            bar.Children.Add(change);
            stack.Children.Add(bar);
            stack.Children.Add(Message("Connect to scan", "Your browser opens Google's consent page. AI Pulse asks for Drive access so it can list sizes and move what you choose to the trash. Nothing is changed until you confirm a delete."));
            return stack;
        }
        var scan = Btn(driveRoot == null ? "Scan Drive" : "Rescan"); scan.Click += (_, _) => DriveRun("Listing your Drive", DriveScan); bar.Children.Add(scan);
        var disconnect = Btn("Disconnect"); ToolTip.SetTip(disconnect, "Forget the sign-in on this PC and revoke it at Google");
        disconnect.Click += async (_, _) => { await Drive.Disconnect(); driveRoot = driveCurrent = null; driveItems = []; driveQuota = null; driveNotice = "Disconnected from Google Drive."; RenderBody(); };
        bar.Children.Add(disconnect);
        stack.Children.Add(bar);

        if (driveQuota is { } q)
        {
            var cards = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 12 };
            cards.Children.Add(Metric("ACCOUNT STORAGE", DiskScanner.Bytes(q.Usage), q.Limit is long limit ? $"of {DiskScanner.Bytes(limit)} · shared with Gmail and Photos" : "No storage limit"));
            cards.Children.Add(Metric("DRIVE FILES", DiskScanner.Bytes(q.UsageInDrive), "Files you own in Drive"));
            cards.Children.Add(Metric("DRIVE TRASH", DiskScanner.Bytes(q.UsageInTrash), q.UsageInTrash > 0 ? "Still counts until the trash is emptied in Drive" : "Trash is empty", q.UsageInTrash > 0 ? "#e8b65f" : null));
            stack.Children.Add(cards);
        }
        if (driveCurrent == null) { stack.Children.Add(Message("Scan to see what uses the space", "Listing reads names, sizes and folders only. It never opens or downloads your files.")); return stack; }

        var largest = DriveTree.LargestFiles(driveItems, 15);
        if (largest.Count > 0)
        {
            stack.Children.Add(Text("Largest files", 15, null, true));
            var list = new StackPanel();
            foreach (var file in largest) list.Children.Add(DriveFileRow(file));
            stack.Children.Add(Card(list));
        }
        var act = new RowActions(driveRoot, n => { driveCurrent = n; RenderBody(); }, n => OpenUrl(DriveUrl(n.RemoteId)), "Open in Google Drive", DriveDeleteFolder);
        stack.Children.Add(DiskBreadcrumb(driveCurrent, n => { driveCurrent = n; RenderBody(); }));
        var rows = new StackPanel();
        foreach (var child in driveCurrent.Children.Take(DiskRows)) rows.Children.Add(DiskRow(child, showPath: false, act));
        if (driveCurrent.OwnFileBytes > 0) rows.Children.Add(Text($"Files directly in this folder: {DiskScanner.Bytes(driveCurrent.OwnFileBytes)}", 12, "#8ba0bd"));
        if (driveCurrent.Children.Count == 0 && driveCurrent.OwnFileBytes == 0) rows.Children.Add(Text("Nothing here uses space.", 12, "#8ba0bd"));
        stack.Children.Add(Card(rows));
        return stack;
    }

    Control DriveSetup()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("Set up once: your own Google OAuth client", 15, null, true));
        panel.Children.Add(Text(
            "1. In Google Cloud Console, create a project (or pick one) and enable the Google Drive API.\n" +
            "2. Under Google Auth Platform, set up the consent screen as External and add your Google account as a test user.\n" +
            "3. Under Clients, create an OAuth client of type Desktop app.\n" +
            "4. Paste its client ID and client secret here.", 13));
        panel.Children.Add(Text("AI Pulse talks to Google directly from this PC. The client secret of a desktop app is not a real secret, and your sign-in is stored encrypted for your Windows user.", 11, "#8ea3c0"));
        var id = new TextBox { PlaceholderText = "Client ID (ends in .apps.googleusercontent.com)", Text = localService.Settings.DriveClientId };
        var secret = new TextBox { PlaceholderText = "Client secret", PasswordChar = '•', Text = localService.Settings.DriveClientSecret };
        Avalonia.Automation.AutomationProperties.SetName(id, "Google OAuth client ID"); Avalonia.Automation.AutomationProperties.SetName(secret, "Google OAuth client secret");
        panel.Children.Add(id); panel.Children.Add(secret);
        var save = Btn("Save client");
        save.Click += (_, _) =>
        {
            string clientId = (id.Text ?? "").Trim();
            if (!DriveAccount.Configured(clientId)) { driveNotice = "That client ID does not end in .apps.googleusercontent.com."; RenderBody(); return; }
            localService.Settings.DriveClientId = clientId; localService.Settings.DriveClientSecret = (secret.Text ?? "").Trim();
            Paths.Save(localService.Settings); driveAccount = null; driveNotice = ""; RenderBody();
        };
        panel.Children.Add(save);
        return Card(panel);
    }

    Control DriveFileRow(DriveItem file)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2.4*,90,Auto"), Margin = new Thickness(0, 5) };
        var name = new StackPanel { Spacing = 2 };
        name.Children.Add(Text(file.Name, 13, null, true));
        name.Children.Add(Text(file.Modified.Year > 1980 ? $"changed {file.Modified.ToLocalTime():MMM d yyyy}" : "", 11, "#8ba0bd"));
        grid.Children.Add(name);
        var size = Text(DiskScanner.Bytes(file.Bytes), 13, null, true); size.HorizontalAlignment = HorizontalAlignment.Right; size.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(size, 1); grid.Children.Add(size);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var open = Btn("Show"); ToolTip.SetTip(open, "Open in Google Drive"); open.Click += (_, _) => OpenUrl("https://drive.google.com/file/d/" + Uri.EscapeDataString(file.Id) + "/view");
        actions.Children.Add(open);
        if (file.CanTrash) { var delete = Btn("Delete…"); delete.Click += async (_, _) => await DriveDeleteFile(file); actions.Children.Add(delete); }
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        return grid;
    }

    static string DriveUrl(string? folderId) => folderId == null ? "https://drive.google.com/drive/my-drive" : "https://drive.google.com/drive/folders/" + Uri.EscapeDataString(folderId);

    static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception e) { Paths.Log("open-url", e); } }

    void DriveRun(string what, Func<CancellationToken, Task> work)
    {
        if (driveWork != null) return;
        driveWork = new CancellationTokenSource(); driveBusy = what; driveListed = 0; driveNotice = "";
        var token = driveWork.Token;
        RenderBody();
        _ = Task.Run(async () =>
        {
            string? error = null;
            try { await work(token); }
            catch (OperationCanceledException) { error = "Stopped."; }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or System.Net.HttpListenerException or System.Text.Json.JsonException) { error = e.Message; Paths.Log("drive", e); }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (error != null) driveNotice = error;
                driveWork?.Dispose(); driveWork = null;
                if (page == "Drive") RenderBody();
            });
        });
    }

    Task DriveConnect(CancellationToken cancel) => Drive.Connect(OpenUrl, cancel);

    async Task DriveScan(CancellationToken cancel)
    {
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        ticker.Tick += (_, _) => { if (page == "Drive") RenderBody(); };
        await Dispatcher.UIThread.InvokeAsync(ticker.Start);
        try
        {
            var quota = await Drive.Quota(cancel);
            string rootId = await Drive.RootId(cancel);
            var items = await Drive.ListAll(n => driveListed = n, cancel);
            var tree = DriveTree.Build(items, rootId);
            await Dispatcher.UIThread.InvokeAsync(() => { driveQuota = quota; driveItems = items; driveRoot = driveCurrent = tree; });
        }
        finally { await Dispatcher.UIThread.InvokeAsync(ticker.Stop); }
    }

    async Task DriveDeleteFolder(DiskNode node)
    {
        if (node.RemoteId is not { } id || !node.CanDelete) return;
        if (!await ConfirmDelete("Move to Drive trash", "Move this Drive folder to the trash?", node.FullPath,
                $"{DiskScanner.Bytes(node.Size)} · {Count(node.Files, "file")} · {Count(node.Folders, "folder")}",
                "Drive keeps trashed items for 30 days, and you can restore them from Drive's trash. They still use storage until the trash is emptied. If others can see this folder, they lose access to it.",
                "Move to Drive trash")) return;
        await DriveTrash(id, node.FullPath, node.Size, () =>
        {
            var gone = new HashSet<string>();
            void Walk(DiskNode n) { if (n.RemoteId != null) gone.Add(n.RemoteId); foreach (var c in n.Children) Walk(c); }
            Walk(node);
            driveItems = driveItems.Where(i => !gone.Contains(i.Id) && !(i.Parents.Length > 0 && gone.Contains(i.Parents[0]))).ToList();
            if (driveCurrent == node) driveCurrent = node.Parent;
            DiskScanner.Remove(node);
        });
    }

    async Task DriveDeleteFile(DriveItem file)
    {
        if (!await ConfirmDelete("Move to Drive trash", "Move this file to Drive's trash?", file.Name, DiskScanner.Bytes(file.Bytes),
                "Drive keeps trashed items for 30 days, and you can restore them from Drive's trash. They still use storage until the trash is emptied.",
                "Move to Drive trash")) return;
        await DriveTrash(file.Id, file.Name, file.Bytes, () =>
        {
            driveItems.Remove(file);
            if (driveRoot != null) driveRoot = driveCurrent = DriveTree.Build(driveItems, driveRoot.RemoteId!);
        });
    }

    async Task DriveTrash(string id, string label, long bytes, Action update)
    {
        driveNotice = "Moving " + label + " to Drive's trash…"; RenderBody();
        try
        {
            await Drive.Trash(id, CancellationToken.None);
            update();
            if (driveQuota is { } q) driveQuota = q with { UsageInDrive = q.UsageInDrive - bytes, UsageInTrash = q.UsageInTrash + bytes };
            driveNotice = $"Moved {label} ({DiskScanner.Bytes(bytes)}) to Drive's trash. Empty the trash in Drive to free the space.";
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException) { driveNotice = $"{label} was not moved. {e.Message}"; Paths.Log("drive-trash", e); }
        RenderBody();
    }
}
