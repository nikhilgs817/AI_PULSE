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
    public FleetDashboard(FleetConnection connection)=>this.connection=connection;
    public MonitorService? SelectedMonitor {get{lock(gate)return monitors.GetValueOrDefault(SelectedHost);}}
    public void Start()=>task=Task.Run(Run);
    async Task Run()
    {
        using var client=FleetFiles.Client(connection);
        while(!stop.IsCancellationRequested)
        {
            try
            {
                Hosts=await client.GetFromJsonAsync<List<HostSummary>>("/fleet/v1/hosts",stop.Token)??[];LastContact=J.Now;Status="Hub connected";
                string id=SelectedHost;if(id!=""&&Hosts.Any(h=>h.Id==id))
                {
                    if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException();
                    MonitorService monitor;
                    lock(gate)
                    {
                        if(!monitors.TryGetValue(id,out monitor!))
                        {
                            var settings=new AppSettings{EnableGateway=false,ReadAgentUsage=false};
                            monitor=new(settings,Path.Combine(Paths.Root,"fleet-cache",J.Hash(connection.Url)[..16],id,"pulse.db"),remote:true);monitors[id]=monitor;
                        }
                    }
                    for(int i=0;i<8;i++)
                    {
                        long.TryParse(monitor.Store.SyncState("dashboard-cursor"),out long after);
                        var view=await client.GetFromJsonAsync<HostView>($"/fleet/v1/hosts/{id}?after={after}",stop.Token)??throw new InvalidDataException();
                        string previous=monitor.Store.SyncState("hub-id");if(previous!=""&&previous!=view.HubId)throw new InvalidDataException("Hub identity changed. Reconnect with a new profile.");
                        monitor.Store.SyncState("hub-id",view.HubId);monitor.Store.ApplyBatch("dashboard",view.Events,false);
                        if(view.Events.Count>0)monitor.Store.SyncState("dashboard-cursor",view.Events.Max(e=>e.Sequence).ToString());
                        if(view.Snapshot!=null)monitor.ApplySnapshot(view.Snapshot,view.Host.Status=="Live");
                        CatchingUp=view.Events.Count>0;Status=CatchingUp?"Hub connected · history syncing":"Hub connected · history current";
                        if(view.Events.Count==0)break;
                    }
                    monitor.Store.Retain(J.Now);
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
    public void Dispose()
    {stop.Cancel();try{task?.Wait(5000);}catch{}lock(gate)foreach(var m in monitors.Values)m.Dispose();stop.Dispose();}
}
