using System.Net.Http.Json;

namespace AiPulse;

public sealed class FleetDashboard : IDisposable
{
    readonly FleetConnection connection;
    readonly CancellationTokenSource stop=new();
    readonly Dictionary<string,MonitorService> monitors=[];
    readonly object gate=new();
    Task? task;
    public string SelectedHost {get;set;}="";
    public List<HostSummary> Hosts {get;private set;}=[];
    public string Status {get;private set;}="Connecting to hub";
    public bool CatchingUp {get;private set;}
    public long LastContact {get;private set;}
    public FleetDashboard(FleetConnection connection)
    {
        this.connection=connection;
        try{if(File.Exists(HostCacheFile))Hosts=FleetFiles.Read<List<HostSummary>>(HostCacheFile).Select(h=>h with{Status="Unknown · hub unavailable"}).ToList();}catch{}
    }
    string HostCacheFile=>Path.Combine(Paths.Root,"fleet-cache",J.Hash(connection.Url)[..16],"hosts.json");
    public MonitorService? SelectedMonitor {get{lock(gate)return monitors.GetValueOrDefault(SelectedHost);}}
    public void Start(){if(SelectedHost!="")EnsureMonitor(SelectedHost);task=Task.Run(Run);}
    MonitorService EnsureMonitor(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException();
        lock(gate)
        {
            if(!monitors.TryGetValue(id,out var monitor))
            {
                monitor=new(new AppSettings{EnableGateway=false,ReadAgentUsage=false},Path.Combine(Paths.Root,"fleet-cache",J.Hash(connection.Url)[..16],id,"pulse.db"),remote:true);monitors[id]=monitor;
                string cached=monitor.Store.SyncState("last-snapshot");if(cached!="")try{var snapshot=System.Text.Json.JsonSerializer.Deserialize<HostSnapshot>(cached);if(snapshot!=null)monitor.ApplySnapshot(snapshot,false);}catch{}
            }
            return monitor;
        }
    }
    Task Run()=>Task.WhenAll(PollLive(),PollHistory());
    async Task PollLive()
    {
        using var client=FleetFiles.Client(connection);client.Timeout=TimeSpan.FromSeconds(8);
        while(!stop.IsCancellationRequested)
        {
            try
            {
                Hosts=await client.GetFromJsonAsync<List<HostSummary>>("/fleet/v1/hosts",stop.Token)??[];LastContact=J.Now;FleetFiles.Write(HostCacheFile,Hosts);
                Status=CatchingUp?"Hub connected · history syncing":"Hub connected · history current";
                string id=SelectedHost;
                if(id!=""&&Hosts.Any(h=>h.Id==id))
                {
                    var monitor=EnsureMonitor(id);var view=await client.GetFromJsonAsync<HostView>("/fleet/v1/state/"+id,stop.Token)??throw new InvalidDataException();
                    string previous=monitor.Store.SyncState("hub-id");if(previous!=""&&previous!=view.HubId)throw new InvalidDataException("Hub identity changed. Reconnect with a new profile.");
                    if(view.Snapshot!=null){monitor.Store.SyncState("last-snapshot",System.Text.Json.JsonSerializer.Serialize(view.Snapshot));monitor.ApplySnapshot(view.Snapshot,view.Host.Status=="Live");}
                }
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception e)
            {
                Status="Hub unavailable · cached history · "+e.GetType().Name;Hosts=Hosts.Select(h=>h with{Status="Unknown · hub unavailable"}).ToList();
                lock(gate)foreach(var m in monitors.Values)if(m.RemoteSnapshot is {} snapshot)m.ApplySnapshot(snapshot,false);
            }
            try{await Task.Delay(2000,stop.Token);}catch(OperationCanceledException){}
        }
    }
    async Task PollHistory()
    {
        using var client=FleetFiles.Client(connection);
        while(!stop.IsCancellationRequested)
        {
            try
            {
                string id=SelectedHost;
                if(id!=""&&Hosts.Any(h=>h.Id==id))
                {
                    var monitor=EnsureMonitor(id);long.TryParse(monitor.Store.SyncState("dashboard-cursor"),out long after);
                    var view=await client.GetFromJsonAsync<HostView>($"/fleet/v1/hosts/{id}?after={after}",stop.Token)??throw new InvalidDataException();
                    string previous=monitor.Store.SyncState("hub-id");if(previous!=""&&previous!=view.HubId)throw new InvalidDataException("Hub identity changed. Reconnect with a new profile.");
                    if(view.Snapshot!=null)monitor.Store.RetentionDays=view.Snapshot.RetentionDays;monitor.Store.SyncState("hub-id",view.HubId);monitor.Store.ApplyBatch("dashboard",view.Events,false);
                    if(view.Events.Count>0)monitor.Store.SyncState("dashboard-cursor",view.Events.Max(e=>e.Sequence).ToString());
                    CatchingUp=view.Events.Count>0;monitor.Store.Retain(J.Now);
                    if(CatchingUp){await Task.Delay(100,stop.Token);continue;}
                }
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception e){CatchingUp=true;Paths.Log("fleet-query-history",e);}
            try{await Task.Delay(2000,stop.Token);}catch(OperationCanceledException){}
        }
    }
    public void Dispose()
    {stop.Cancel();try{task?.Wait(5000);}catch{}lock(gate)foreach(var m in monitors.Values)m.Dispose();stop.Dispose();}
}
