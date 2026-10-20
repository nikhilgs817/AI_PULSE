using System.Text.Json;

namespace AiPulse;

public sealed class MonitorService : IDisposable
{
    public AppSettings Settings {get;}
    public Storage Store {get;}
    public Hardware Hardware {get;private set;}=null!;
    public Ollama Ollama {get;}=new();
    public AgentUsage Agents {get;}
    public Gateway Gateway {get;}
    public SensorBridge SensorBridge {get;}=new();
    public List<Reading> Readings {get;private set;}=[];
    public long LastSample{get;private set;}
    public string Health{get;private set;}="Starting";
    readonly CancellationTokenSource stop=new();
    Task? hardwareTask,modelTask,agentTask;
    readonly Dictionary<string,long> breaches=[];
    public bool IsRemote {get;}
    public bool RemoteLive {get;private set;}
    public bool Fresh=>LastSample>0&&Math.Abs(J.Now-LastSample)<=(IsRemote?10:8)&&(!IsRemote||RemoteLive);
    public HostSnapshot? RemoteSnapshot {get;private set;}
    public string HostName=>RemoteSnapshot?.Name??Environment.MachineName;
    public string HostPlatform=>RemoteSnapshot?.Platform??(OperatingSystem.IsWindows()?"Windows":"Ubuntu / Linux");
    public string CpuName=>RemoteSnapshot?.Cpu??Hardware?.CpuName??"CPU";
    public string GpuName=>RemoteSnapshot?.Gpu??Hardware?.GpuName??"GPU";
    public int LogicalProcessors=>RemoteSnapshot?.LogicalProcessors??Environment.ProcessorCount;
    public List<HardwareItem> Inventory=>RemoteSnapshot?.Inventory??Hardware?.Inventory??[];
    public List<ProcessInfo> Processes=>RemoteSnapshot?.Processes??Hardware?.Processes??[];
    public List<AgentResource> AgentApps=>RemoteSnapshot?.AgentApps??Hardware?.AgentApps??[];
    public DateTime ProcessSampleTime=>IsRemote&&!RemoteLive?default:RemoteSnapshot?.ProcessSampleTime??Hardware?.ProcessSampleTime??default;
    public int UnreadableProcesses=>RemoteSnapshot?.UnreadableProcesses??Hardware?.UnreadableProcesses??0;
    public string AgentStatus=>RemoteSnapshot?.AgentStatus??Agents.Status;
    public string GatewayStatus=>RemoteSnapshot?.GatewayStatus??Gateway.Status;
    public MonitorService(AppSettings settings,string? database=null,bool replication=false,bool remote=false)
    {Settings=settings;IsRemote=remote;Store=new(database??Paths.Database,settings.RetentionDays,replication);Agents=new(Store,settings.UsageRoots);Gateway=new(Store,Ollama,settings);}
    public void ApplySnapshot(HostSnapshot snapshot,bool live)
    {
        RemoteLive=live;RemoteSnapshot=snapshot;Readings=live?snapshot.Readings: snapshot.Readings.Select(r=>r with{Value=null}).ToList();LastSample=snapshot.SampledAt;Health=snapshot.Health;
        Ollama.ApplySnapshot(live?snapshot.Models:[],snapshot.Installed,live?snapshot.ModelStatus: new(){[snapshot.Name]="Offline / stale"},live?snapshot.ActiveRequests:null);
        Settings.ReadAgentUsage=snapshot.ReadAgentUsage;Settings.RetentionDays=snapshot.RetentionDays;Store.RetentionDays=snapshot.RetentionDays;Settings.LastBackup=snapshot.LastBackup;Settings.LastBackupError=snapshot.BackupError;
    }
    public HostSnapshot Snapshot()
    {
        var q=Store.QueueInfo();return new(){Name=HostName,Platform=HostPlatform,Cpu=CpuName,Gpu=GpuName,LogicalProcessors=LogicalProcessors,SampledAt=LastSample,Health=Health,AgentStatus=AgentStatus,GatewayStatus=GatewayStatus,ReadAgentUsage=Settings.ReadAgentUsage,RetentionDays=Settings.RetentionDays,LastBackup=Settings.LastBackup,BackupError=Settings.LastBackupError,Readings=Readings,Inventory=Inventory,Processes=Processes,AgentApps=AgentApps,ProcessSampleTime=ProcessSampleTime,UnreadableProcesses=UnreadableProcesses,Models=Ollama.Models,Installed=Ollama.Installed,ModelStatus=Ollama.Status,ActiveRequests=Ollama.Active.ToDictionary(),QueueCount=q.Count,QueueBytes=q.Bytes};
    }
    public void Start()
    {
        if(IsRemote)return;
        hardwareTask=Task.Run(async()=>
        {
            try{Hardware=new Hardware();}catch(Exception e){Paths.Log("collector-start",e);Health="Hardware collector unavailable";return;}
            long maintenance=0;
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    var raw=Hardware.Sample();
                    if(J.Now-SensorBridge.LastSeen<8){var deeper=SensorBridge.Readings.Where(r=>r.Value.HasValue).ToList();raw.RemoveAll(r=>deeper.Any(d=>d.Id==r.Id));raw.AddRange(deeper);}
                    var rows=raw.GroupBy(r=>r.Id).Select(g=>g.First()).ToList();long sampled=J.Now;Store.Sample(sampled,rows);Readings=rows;LastSample=sampled;Health=Hardware.Status;
                    CheckAlerts();
                    if(J.Now-maintenance>3600){Store.Retain(J.Now);maintenance=J.Now;await EnsureBackup();}
                }catch(Exception e){Paths.Log("collector",e);Health="Collector error · check diagnostics";}
                try{await Task.Delay(2000,stop.Token);}catch(OperationCanceledException){}
            }
        });
        modelTask=Task.Run(async()=>{await Gateway.Start(stop.Token);while(!stop.IsCancellationRequested){try{if(!Gateway.Status.StartsWith("Listening"))await Gateway.Start(stop.Token);await Ollama.Poll(Settings,stop.Token);}catch(Exception e){Paths.Log("model-discovery",e);}try{await Task.Delay(4000,stop.Token);}catch(OperationCanceledException){}}});
        agentTask=Task.Run(async()=>{while(!stop.IsCancellationRequested){try{if(Settings.ReadAgentUsage)Agents.Poll();}catch(Exception e){Paths.Log("agent-import",e);}try{await Task.Delay(8000,stop.Token);}catch(OperationCanceledException){}}});
    }
    public double? Value(string id)=>Readings.FirstOrDefault(r=>r.Id==id)?.Value;
    void CheckAlerts()
    {
        void Sustained(string key,string title,string detail,bool bad,int seconds=30)
        {
            if(bad){if(!breaches.ContainsKey(key))breaches[key]=J.Now;if(J.Now-breaches[key]>=seconds)Store.Alert(key,title,detail,true);}
            else{breaches.Remove(key);Store.Alert(key,title,detail,false);}
        }
        Sustained("gpu-temperature","GPU temperature elevated","Above your configured "+Settings.TemperatureWarning+" °C threshold for 30 seconds.",Value("gpu.temperature")>=Settings.TemperatureWarning);
        Sustained("cpu-temperature","CPU temperature elevated","Above your configured "+Settings.TemperatureWarning+" °C threshold for 30 seconds.",Value("cpu.temperature")>=Settings.TemperatureWarning);
        Sustained("memory","Memory pressure","Less than 5% of physical memory remains available.",Value("ram.load")>95);
        var drive=new DriveInfo(Path.GetPathRoot(Paths.Root)!);Sustained("storage","Monitoring storage running low","Less than 5 GiB free on the data drive.",drive.AvailableFreeSpace<5L*1024*1024*1024);
    }
    async Task EnsureBackup()
    {
        if(DateTimeOffset.TryParse(Settings.LastBackup,out var last)&&last.UtcDateTime.Date==DateTime.UtcNow.Date)return;
        await BackupNow();
    }
    public Task<string?> BackupNow()=>Task.Run(()=>
    {
        try{var file=Store.Backup(Paths.Backups(Settings));Settings.LastBackup=DateTimeOffset.UtcNow.ToString("O");Settings.LastBackupError=null;Paths.Save(Settings);Store.Alert("backup","Backup failure","",false);return (string?)file;}
        catch(Exception e){Paths.Log("backup",e);Settings.LastBackupError="Backup failed: "+e.GetType().Name;Paths.Save(Settings);Store.Alert("backup","Backup failure",Settings.LastBackupError,true);return null;}
    });
    public string Export()
    {
        var file=Path.Combine(Paths.Root,"exports","usage-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".csv");
        Store.ExportUsage(file,J.Now-Settings.RetentionDays*86400L);
        return file;
    }
    public void Dispose()
    {
        stop.Cancel();try{Task.WaitAll(new[]{hardwareTask,modelTask,agentTask}.Where(x=>x!=null).Cast<Task>().ToArray(),6000);}catch{}
        try{Gateway.DisposeAsync().AsTask().Wait(2000);}catch{}
        SensorBridge.Dispose();Hardware?.Dispose();Ollama.Dispose();Store.Dispose();stop.Dispose();
    }
}
