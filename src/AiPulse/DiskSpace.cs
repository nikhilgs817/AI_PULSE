using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiPulse;

// WinDirStat-style folder sizes. The scan only reads directory listings; it never opens files.
// Deletes go to the Recycle Bin through the Windows shell, after the caller has confirmed.
public sealed class DiskNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public DiskNode? Parent { get; init; }
    public long Size { get; internal set; }
    public long Files { get; internal set; }
    public long Folders { get; internal set; }
    /// <summary>Bytes in files directly inside this folder (not in subfolders).</summary>
    public long OwnFileBytes { get; internal set; }
    public DateTime LastWriteUtc { get; internal set; }
    public bool Denied { get; internal set; }
    public string? Label { get; internal set; }
    public List<DiskNode> Children { get; } = [];
    public double ShareOfParent => Parent is { Size: > 0 } p ? (double)Size / p.Size : 1;
}

public sealed class DiskScanProgress
{
    long files, bytes;
    public long FilesSeen => Interlocked.Read(ref files);
    public long BytesSeen => Interlocked.Read(ref bytes);
    internal void Add(long fileCount, long byteCount) { Interlocked.Add(ref files, fileCount); Interlocked.Add(ref bytes, byteCount); }
}

public static class DiskScanner
{
    static readonly EnumerationOptions Listing = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
        AttributesToSkip = 0, MatchType = MatchType.Simple,
    };

    /// <summary>
    /// Builds the folder tree under <paramref name="root"/>. Junctions and symbolic links are listed
    /// with no size and are not followed, so nothing is counted twice and scans cannot loop.
    /// </summary>
    public static DiskNode Scan(string root, CancellationToken cancel = default, DiskScanProgress? progress = null)
    {
        var info = new DirectoryInfo(root);
        var node = new DiskNode { Name = info.FullName, FullPath = info.FullName, LastWriteUtc = SafeTime(info) };
        Fill(node, info, depth: 0, cancel, progress);
        return node;
    }

    static void Fill(DiskNode node, DirectoryInfo dir, int depth, CancellationToken cancel, DiskScanProgress? progress)
    {
        cancel.ThrowIfCancellationRequested();
        var subdirs = new List<DirectoryInfo>();
        long ownBytes = 0, ownFiles = 0;
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos("*", Listing))
            {
                if (entry is DirectoryInfo d)
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    subdirs.Add(d);
                }
                else if (entry is FileInfo f && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    try { ownBytes += f.Length; ownFiles++; } catch (IOException) { }
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { node.Denied = true; }
        progress?.Add(ownFiles, ownBytes);
        var children = subdirs.Select(d => new DiskNode { Name = d.Name, FullPath = d.FullName, Parent = node, LastWriteUtc = SafeTime(d) }).ToArray();
        // Parallel near the top, where folders are big and independent; serial deeper down to avoid oversubscription.
        if (depth < 2 && children.Length > 1)
            Parallel.For(0, children.Length, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                i => Fill(children[i], subdirs[i], depth + 1, cancel, progress));
        else
            for (int i = 0; i < children.Length; i++) Fill(children[i], subdirs[i], depth + 1, cancel, progress);
        node.OwnFileBytes = ownBytes;
        node.Size = ownBytes + children.Sum(c => c.Size);
        node.Files = ownFiles + children.Sum(c => c.Files);
        node.Folders = children.Length + children.Sum(c => c.Folders);
        node.Label = DiskLabels.For(node.FullPath);
        node.Children.AddRange(children.OrderByDescending(c => c.Size));
    }

    static DateTime SafeTime(FileSystemInfo info) { try { return info.LastWriteTimeUtc; } catch (IOException) { return default; } }

    /// <summary>Takes a deleted folder out of the tree and subtracts its totals from every ancestor.</summary>
    public static void Remove(DiskNode node)
    {
        if (node.Parent is not { } parent) return;
        parent.Children.Remove(node);
        for (var p = parent; p != null; p = p.Parent)
        {
            p.Size -= node.Size; p.Files -= node.Files; p.Folders -= node.Folders + 1;
            if (p.Parent is { } gp) { gp.Children.Sort((a, b) => b.Size.CompareTo(a.Size)); }
        }
    }

    /// <summary>The largest labelled folders anywhere in the tree, outermost match only.</summary>
    public static List<DiskNode> Labelled(DiskNode root, int limit = 15)
    {
        var found = new List<DiskNode>();
        void Walk(DiskNode n) { if (n.Label != null && n.Parent != null) { found.Add(n); return; } foreach (var c in n.Children) Walk(c); }
        Walk(root);
        return found.OrderByDescending(n => n.Size).Take(limit).ToList();
    }

    public static string Bytes(long b) => b switch
    {
        >= 1L << 40 => $"{b / (double)(1L << 40):0.00} TB",
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}

// Names the folders that LLM and ML work tends to fill, so they are easy to find in a big tree.
public static class DiskLabels
{
    static readonly (string[] Tail, string Label)[] Known =
    [
        (["huggingface", "hub"], "Hugging Face models"),
        (["huggingface"], "Hugging Face cache"),
        ([".ollama", "models"], "Ollama models"),
        ([".lmstudio", "models"], "LM Studio models"),
        (["lm studio", "models"], "LM Studio models"),
        (["torch", "hub"], "PyTorch hub cache"),
        ([".cache", "torch"], "PyTorch cache"),
        (["pip", "cache"], "pip cache"),
        ([".cache", "pip"], "pip cache"),
        (["uv", "cache"], "uv cache"),
        (["conda", "pkgs"], "Conda package cache"),
        (["anaconda3", "pkgs"], "Conda package cache"),
        (["miniconda3", "pkgs"], "Conda package cache"),
        (["miniforge3", "pkgs"], "Conda package cache"),
        (["docker", "wsl"], "Docker WSL disk"),
        (["docker desktop", "vm-data"], "Docker disk image"),
        (["nvidia", "computecache"], "NVIDIA shader cache"),
        (["mlruns"], "MLflow runs"),
        (["wandb"], "Weights & Biases runs"),
        (["checkpoints"], "Model checkpoints"),
        (["node_modules"], "Node packages"),
        ([".venv"], "Python virtual environment"),
        (["venv"], "Python virtual environment"),
        (["__pycache__"], "Python bytecode cache"),
    ];

    public static string? For(string path)
    {
        var parts = path.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (tail, label) in Known)
        {
            if (parts.Length < tail.Length) continue;
            bool match = true;
            for (int i = 0; i < tail.Length && match; i++)
                match = string.Equals(parts[parts.Length - tail.Length + i], tail[i], StringComparison.OrdinalIgnoreCase);
            if (match) return label;
        }
        return null;
    }
}

public static class DiskSafety
{
    /// <summary>
    /// Why a folder must not be deleted, or null when it may go to the Recycle Bin. A folder is
    /// protected when it is a drive root, holds a protected location, or sits inside a system tree.
    /// </summary>
    public static string? Blocked(string path, IEnumerable<string> keep, IEnumerable<string> systemTrees)
    {
        string full = Normal(path);
        if (Path.GetPathRoot(full) is { } root && Normal(root) == full) return "It is the root of a drive.";
        foreach (var tree in systemTrees.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Normal))
            if (Same(full, tree) || Inside(full, tree) || Inside(tree, full)) return "It is or holds a system folder (" + tree + ").";
        foreach (var k in keep.Where(k => !string.IsNullOrWhiteSpace(k)).Select(Normal))
            if (Same(full, k) || Inside(k, full)) return "It is or holds " + k + ", which AI Pulse keeps.";
        var name = Path.GetFileName(full);
        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
            return "It is a Windows system folder.";
        return null;
    }

    /// <summary>Locations that are never deleted from AI Pulse, on this machine.</summary>
    public static (List<string> Keep, List<string> SystemTrees) Defaults()
    {
        string F(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return ""; } }
        var systemTrees = new List<string> { F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86) };
        if (!OperatingSystem.IsWindows()) systemTrees.AddRange(["/bin", "/boot", "/etc", "/lib", "/lib64", "/proc", "/sbin", "/sys", "/usr", "/var/lib"]);
        var keep = new List<string>
        {
            F(Environment.SpecialFolder.UserProfile), F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.Desktop), F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.MyPictures), F(Environment.SpecialFolder.MyMusic), F(Environment.SpecialFolder.MyVideos),
            Path.Combine(F(Environment.SpecialFolder.UserProfile), "Downloads"), AppContext.BaseDirectory, Paths.Root,
        };
        return (keep, systemTrees);
    }

    static string Normal(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static bool Same(string a, string b) => string.Equals(a, b, Cmp);
    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    static bool Inside(string child, string parent) =>
        child.Length > parent.Length && child.StartsWith(parent, Cmp) && (child[parent.Length] == Path.DirectorySeparatorChar || child[parent.Length] == Path.AltDirectorySeparatorChar || parent.EndsWith(Path.DirectorySeparatorChar));
}

public static class RecycleBin
{
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>
    /// Moves a folder to the Recycle Bin. Windows asks before deleting anything permanently (for
    /// example when the folder is too big for the Recycle Bin), and the user can say no there.
    /// Returns null on success, or what went wrong.
    /// </summary>
    public static string? Send(string path)
    {
        if (!OperatingSystem.IsWindows()) return "The Recycle Bin is only available on Windows.";
        if (!Directory.Exists(path)) return "The folder is no longer there.";
        string? error = null;
        var thread = new Thread(() => error = ShellDelete(path));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error == null && Directory.Exists(path)) error = "Windows kept the folder. Some files may be open in another program.";
        return error;
    }

    [SupportedOSPlatform("windows")]
    static string? ShellDelete(string path)
    {
        var op = new ShFileOp
        {
            Func = FoDelete,
            From = Path.GetFullPath(path) + "\0\0",
            // Undo = Recycle Bin. No routine "are you sure" (AI Pulse asked already), but Windows still
            // warns before any permanent delete, which FOF_WANTNUKEWARNING keeps even with NOCONFIRMATION.
            Flags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning,
        };
        int result = SHFileOperation(ref op);
        if (op.AnyOperationsAborted) return "Cancelled. Nothing was deleted permanently.";
        return result == 0 ? null : $"Windows could not move it (code 0x{result:X}).";
    }

    const uint FoDelete = 3;
    const ushort FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int SHFileOperation(ref ShFileOp op);
}
