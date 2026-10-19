using System.Runtime.InteropServices;

namespace AiPulse;

public record AgentProcess(int Id, int ParentId, string Name, long Started, double? Cpu, double MemoryMiB);
public record AgentResource(string App, List<AgentProcess> Processes)
{
    public double? Cpu => Processes.All(p=>p.Cpu.HasValue) ? Processes.Sum(p=>p.Cpu!.Value) : null;
    public double MemoryMiB => Processes.Sum(p=>p.MemoryMiB);
}

public static class AgentResources
{
    static string? App(string name) => name.ToLowerInvariant() switch
    {
        "codex" or "codex.exe" => "Codex",
        "claude" or "claude.exe" => "Claude",
        "ollama" or "ollama.exe" or "ollama app" or "ollama app.exe" => "Ollama",
        _ => null
    };

    // Assign each process once, to its nearest named app ancestor. Session logs do not
    // identify OS processes, so these are app totals, never invented per-session shares.
    public static List<AgentResource> Group(IEnumerable<AgentProcess> processes)
    {
        var byId=processes.DistinctBy(p=>p.Id).ToDictionary(p=>p.Id);
        var groups=new Dictionary<string,List<AgentProcess>>();
        foreach(var process in byId.Values)
        {
            var current=process;var seen=new HashSet<int>();
            while(seen.Add(current.Id))
            {
                var app=App(current.Name);
                if(app!=null){if(!groups.TryGetValue(app,out var list))groups[app]=list=[];list.Add(process);break;}
                if(!byId.TryGetValue(current.ParentId,out var parent)||parent.Started>current.Started)break;
                current=parent;
            }
        }
        return groups.OrderBy(g=>g.Key).Select(g=>new AgentResource(g.Key,g.Value.OrderByDescending(p=>p.MemoryMiB).ToList())).ToList();
    }

    public static double? CpuDelta(long started,double seconds,(long Started,double Seconds)? previous,double elapsed,int processors)
        => previous.HasValue&&previous.Value.Started==started&&seconds>=previous.Value.Seconds&&elapsed>0&&processors>0
            ? Math.Clamp((seconds-previous.Value.Seconds)/elapsed/processors*100,0,100) : null;

    public static Dictionary<int,int> Parents()
    {
        var result=new Dictionary<int,int>();
        if(OperatingSystem.IsWindows())
        {
            var handle=CreateToolhelp32Snapshot(2,0);
            if(handle==new IntPtr(-1))return result;
            try
            {
                var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),Exe=""};
                if(Process32FirstW(handle,ref entry))do{result[(int)entry.Id]=(int)entry.ParentId;}while(Process32NextW(handle,ref entry));
            }
            finally{CloseHandle(handle);}
        }
        else
        {
            foreach(var dir in Directory.GetDirectories("/proc"))
            {
                if(!int.TryParse(Path.GetFileName(dir),out var id))continue;
                try{var stat=File.ReadAllText(dir+"/stat");var fields=stat[(stat.LastIndexOf(')')+2)..].Split(' ',StringSplitOptions.RemoveEmptyEntries);result[id]=int.Parse(fields[1]);}catch{}
            }
        }
        return result;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size,Usage,Id;public UIntPtr Heap;public uint Module,Threads,ParentId;public int Priority;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
