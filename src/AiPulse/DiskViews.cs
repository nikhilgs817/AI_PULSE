using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AiPulse;

// The Disk page: which folders use the most space, with delete to the Recycle Bin.
// Scans run on a background thread; the page redraws itself while one runs.
public sealed partial class MainWindow
{
    DiskNode? diskRoot, diskCurrent;
    List<DiskNode> diskLabelled = [];
    DiskScanProgress? diskProgress;
    CancellationTokenSource? diskScan;
    string diskScanning = "", diskNotice = "";
    DispatcherTimer? diskTicker;
    const int DiskRows = 60;

    /// <summary>What a folder row can do. The Disk and Drive pages share the row and pass their own actions.</summary>
    sealed record RowActions(DiskNode? ScanRoot, Action<DiskNode> Open, Action<DiskNode> Show, string ShowTip, Func<DiskNode, Task> Delete);
    RowActions LocalActions => new(diskRoot, n => { diskCurrent = n; RenderBody(); }, n => OpenFolder(n.FullPath), "Show in your file manager", ConfirmDiskDelete);

    Control DiskPage()
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(Heading("Disk space", "Largest folders first · deletes go to the Recycle Bin after you confirm"));
        stack.Children.Add(DiskScanBar());
        if (diskNotice != "") stack.Children.Add(Message("Disk", diskNotice));

        if (diskScan != null && diskProgress != null)
        {
            stack.Children.Add(Message("Scanning " + diskScanning,
                $"{diskProgress.FilesSeen:N0} files · {DiskScanner.Bytes(diskProgress.BytesSeen)} so far. Folders you can't open are skipped."));
            return stack;
        }
        if (diskCurrent == null)
        {
            stack.Children.Add(Message("Pick a drive or folder to scan",
                "AI Pulse reads folder listings only. It opens no files and changes nothing until you delete a folder and confirm."));
            return stack;
        }

        var labelled = diskLabelled;
        if (labelled.Count > 0)
        {
            stack.Children.Add(Text("AI and dev space users found", 15, null, true));
            var list = new StackPanel();
            foreach (var n in labelled) list.Children.Add(DiskRow(n, showPath: true, LocalActions));
            stack.Children.Add(Card(list));
        }

        stack.Children.Add(DiskBreadcrumb(diskCurrent, n => { diskCurrent = n; RenderBody(); }));
        var rows = new StackPanel();
        foreach (var child in diskCurrent.Children.Take(DiskRows)) rows.Children.Add(DiskRow(child, showPath: false, LocalActions));
        int hidden = Math.Max(0, diskCurrent.Children.Count - DiskRows);
        if (diskCurrent.OwnFileBytes > 0 || hidden > 0)
        {
            long rest = diskCurrent.OwnFileBytes + diskCurrent.Children.Skip(DiskRows).Sum(c => c.Size);
            string what = hidden > 0 ? $"Files here and {hidden:N0} smaller folders" : "Files directly in this folder";
            rows.Children.Add(Text($"{what}: {DiskScanner.Bytes(rest)}", 12, "#8ba0bd"));
        }
        if (diskCurrent.Children.Count == 0 && diskCurrent.OwnFileBytes == 0) rows.Children.Add(Text("This folder is empty or could not be read.", 12, "#8ba0bd"));
        stack.Children.Add(Card(rows));
        return stack;
    }

    Control DiskScanBar()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        if (diskScan != null)
        {
            var stop = Btn("Stop scan");
            stop.Click += (_, _) => diskScan?.Cancel();
            bar.Children.Add(stop);
            return bar;
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            string label;
            try { label = $"Scan {drive.Name.TrimEnd('\\')}  ·  {DiskScanner.Bytes(drive.TotalSize - drive.AvailableFreeSpace)} used of {DiskScanner.Bytes(drive.TotalSize)}"; }
            catch (IOException) { continue; }
            var b = Btn(label);
            string root = drive.RootDirectory.FullName;
            b.Click += (_, _) => StartDiskScan(root);
            bar.Children.Add(b);
            if (!OperatingSystem.IsWindows()) break; // Linux lists every mount; the root is enough.
        }
        var pick = Btn("Scan a folder…");
        pick.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to scan", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) StartDiskScan(path);
        };
        bar.Children.Add(pick);
        if (diskRoot != null)
        {
            var again = Btn("Rescan");
            again.Click += (_, _) => StartDiskScan(diskRoot.FullPath);
            bar.Children.Add(again);
        }
        return bar;
    }

    Control DiskBreadcrumb(DiskNode current, Action<DiskNode> open)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (current.Parent is { } parent)
        {
            var up = Btn("↑ Up");
            up.Click += (_, _) => open(parent);
            row.Children.Add(up);
        }
        var diskCurrent = current;
        row.Children.Add(Text($"{diskCurrent.FullPath}  ·  {DiskScanner.Bytes(diskCurrent.Size)}  ·  {diskCurrent.Files:N0} files", 14, null, true));
        return row;
    }

    Control DiskRow(DiskNode node, bool showPath, RowActions act)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2.4*,90,1.2*,Auto"), Margin = new Thickness(0, 5) };
        var name = new StackPanel { Spacing = 2 };
        name.Children.Add(Text(showPath ? node.FullPath : node.Name, 13, null, true));
        string detail = $"{node.Files:N0} files · {node.Folders:N0} folders" + (node.LastWriteUtc.Year > 1980 ? $" · changed {node.LastWriteUtc.ToLocalTime():MMM d yyyy}" : "");
        if (node.Label != null) detail = node.Label + " · " + detail;
        if (node.Denied) detail += " · some contents could not be read";
        name.Children.Add(Text(detail, 11, node.Label != null ? "#72d4e8" : "#8ba0bd"));
        grid.Children.Add(name);

        var size = Text(DiskScanner.Bytes(node.Size), 13, null, true);
        size.HorizontalAlignment = HorizontalAlignment.Right; size.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(size, 1); grid.Children.Add(size);

        double share = showPath ? (act.ScanRoot is { Size: > 0 } r ? (double)node.Size / r.Size : 0) : node.ShareOfParent;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp(share, 0, 1), Height = 8, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(bar, $"{share:P1} of {(showPath ? "the scan" : "this folder")}");
        Grid.SetColumn(bar, 2); grid.Children.Add(bar);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        if (node.Children.Count > 0 || node.OwnFileBytes > 0)
        {
            var open = Btn("Open");
            open.Click += (_, _) => act.Open(node);
            actions.Children.Add(open);
        }
        var show = Btn("Show");
        ToolTip.SetTip(show, act.ShowTip);
        show.Click += (_, _) => act.Show(node);
        actions.Children.Add(show);
        if (node.CanDelete)
        {
            var delete = Btn("Delete…");
            delete.Click += async (_, _) => await act.Delete(node);
            actions.Children.Add(delete);
        }
        Grid.SetColumn(actions, 3); grid.Children.Add(actions);
        return grid;
    }

    void StartDiskScan(string path)
    {
        if (diskScan != null) return;
        diskScan = new CancellationTokenSource();
        diskProgress = new DiskScanProgress();
        diskScanning = path; diskNotice = "";
        var token = diskScan.Token; var progress = diskProgress;
        diskTicker ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        diskTicker.Tick -= DiskTick; diskTicker.Tick += DiskTick; diskTicker.Start();
        RenderBody();
        Task.Run(() => DiskScanner.Scan(path, token, progress), token).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            diskTicker?.Stop();
            if (t.IsCompletedSuccessfully) { diskRoot = diskCurrent = t.Result; diskLabelled = DiskScanner.Labelled(diskRoot); }
            else if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException) diskNotice = "Scan stopped.";
            else { diskNotice = "The scan did not finish: " + t.Exception?.InnerException?.Message; Paths.Log("disk-scan", t.Exception!); }
            diskScan?.Dispose(); diskScan = null; diskProgress = null;
            if (page == "Disk") RenderBody();
        }));
    }

    void DiskTick(object? sender, EventArgs e) { if (page == "Disk") RenderBody(); }

    async Task ConfirmDiskDelete(DiskNode node)
    {
        var (keep, system) = DiskSafety.Defaults();
        if (DiskSafety.Blocked(node.FullPath, keep, system) is { } reason)
        {
            diskNotice = $"{node.FullPath} can't be deleted from AI Pulse. {reason}";
            RenderBody(); return;
        }
        if (!RecycleBin.Supported)
        {
            diskNotice = "Deleting is only available on Windows, where folders go to the Recycle Bin.";
            RenderBody(); return;
        }
        if (!await ConfirmDelete("Move to Recycle Bin", "Move this folder to the Recycle Bin?", node.FullPath,
                $"{DiskScanner.Bytes(node.Size)} · {node.Files:N0} files · {node.Folders:N0} folders" + (node.Label != null ? " · " + node.Label : ""),
                "You can restore it from the Recycle Bin. If it is too big for the Recycle Bin, Windows asks before deleting it for good, and you can say no.",
                "Move to Recycle Bin")) return;

        diskNotice = "Moving " + node.FullPath + " to the Recycle Bin…"; RenderBody();
        string? error = await Task.Run(() => RecycleBin.Send(node.FullPath));
        if (error == null)
        {
            if (diskCurrent == node) diskCurrent = node.Parent;
            DiskScanner.Remove(node);
            diskLabelled = DiskScanner.Labelled(diskRoot!);
            diskNotice = $"Moved {node.FullPath} ({DiskScanner.Bytes(node.Size)}) to the Recycle Bin.";
        }
        else diskNotice = $"{node.FullPath} was not moved. {error}";
        RenderBody();
    }

    /// <summary>The one confirm dialog every delete goes through. Cancel is the default; the delete button names the act.</summary>
    async Task<bool> ConfirmDelete(string title, string question, string path, string facts, string note, string yesLabel)
    {
        var dialog = new Window
        {
            Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Canvas,
        };
        var contents = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        contents.Children.Add(Text(question, 18, null, true));
        contents.Children.Add(Text(path, 13, "#72d4e8", true));
        contents.Children.Add(Text(facts, 13));
        contents.Children.Add(Text(note, 12, "#9eb1ca"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var yes = Btn(yesLabel); yes.Click += (_, _) => dialog.Close(true);
        var no = Btn("Cancel"); no.Click += (_, _) => dialog.Close(false); no.IsDefault = true; no.IsCancel = true;
        buttons.Children.Add(no); buttons.Children.Add(yes);
        contents.Children.Add(buttons);
        dialog.Content = contents;
        return await dialog.ShowDialog<bool>(this);
    }
}
