using System.Net;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiPulse;

public sealed class RegisteredHost
{
    public string Id {get;set;}="";
    public string InstallationId {get;set;}="";
    public string Name {get;set;}="";
    public string TokenHash {get;set;}="";
    public string NetworkType {get;set;}="Private LAN";
    public string EnrollmentHash {get;set;}="";
    public long EnrollmentExpiry {get;set;}
    public bool Revoked {get;set;}
    public long ReceivedAt {get;set;}
    public HostSnapshot? Snapshot {get;set;}
}
public sealed class HubState
{
    public List<RegisteredHost> Hosts {get;set;}=[];
    public Dictionary<string,long> PairingCodes {get;set;}=[];
}
public sealed class FleetHub : IDisposable
{
    readonly string root;
    readonly HubConfig config;
    readonly HubState state;
    readonly object gate=new();
    readonly Dictionary<string,Storage> stores=[];
    readonly Dictionary<string,(string Host,string File,long Created,Storage Reader)> baselines=[];
    readonly Dictionary<string,(long Start,int Count)> enrollmentRates=[];
    public FleetHub(string root)
    {
        this.root=root;config=FleetFiles.Read<HubConfig>(Path.Combine(root,"hub.json"));
        state=File.Exists(Path.Combine(root,"hosts.json"))?FleetFiles.Read<HubState>(Path.Combine(root,"hosts.json")):new();
        // Heartbeats from a prior process are never trusted as a current connection.
        foreach(var host in state.Hosts)host.ReceivedAt=0;
        string transfers=Path.Combine(root,"baseline-transfers");if(Directory.Exists(transfers))foreach(var file in Directory.EnumerateFiles(transfers))
            if(Path.GetExtension(file) is ".db" or ".db-wal" or ".db-shm"&&Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out _))File.Delete(file);
    }
    void Save()=>FleetFiles.Write(Path.Combine(root,"hosts.json"),state);
    Storage Store(RegisteredHost host)
    {
        if(!stores.TryGetValue(host.Id,out var store)){store=new Storage(Path.Combine(root,"hosts",host.Id,"pulse.db"),config.RetentionDays,durable:true);stores[host.Id]=store;}
        return store;
    }
    public bool IsAdmin(string token)=>token.Length>0&&FleetFiles.Matches(token,config.AdminHash);
    public RegisteredHost? AuthenticateHost(string token)
    {lock(gate)return state.Hosts.FirstOrDefault(h=>!h.Revoked&&token.Length>0&&FleetFiles.Matches(token,h.TokenHash));}
    public PairingTicket Pair()
    {
        lock(gate){long now=J.Now;foreach(var key in state.PairingCodes.Where(p=>p.Value<now).Select(p=>p.Key).ToArray())state.PairingCodes.Remove(key);
            if(state.PairingCodes.Count>=100)throw new InvalidOperationException("Too many pending pairing tickets");
            string code=FleetFiles.Secret();long expiry=now+600;state.PairingCodes[J.Hash(code)]=expiry;Save();return new(code,expiry);}
    }
    public EnrollmentResult Enroll(Enrollment request)
    {
        lock(gate)
        {
            if(request.Name.Length is <1 or >100||!Guid.TryParseExact(request.InstallationId,"N",out _)||request.NetworkType is not ("Private LAN" or "VPN" or "Internet"))throw new InvalidDataException("Invalid host identity");
            string hash=J.Hash(request.Code);
            string TokenFor(string id)=>Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config.EnrollmentSecret),System.Text.Encoding.UTF8.GetBytes(id+":"+request.InstallationId))).ToLowerInvariant();
            var retried=state.Hosts.FirstOrDefault(h=>!h.Revoked&&h.EnrollmentHash==hash&&h.InstallationId==request.InstallationId&&h.EnrollmentExpiry>=J.Now);
            if(retried!=null)return new(retried.Id,TokenFor(retried.Id),config.HubId);
            if(!state.PairingCodes.TryGetValue(hash,out var expiry)||expiry<J.Now)throw new UnauthorizedAccessException("Pairing ticket expired or consumed");
            if(state.Hosts.Any(h=>!h.Revoked&&h.InstallationId==request.InstallationId))throw new InvalidOperationException("Installation already paired");
            string id=Guid.NewGuid().ToString("N"),token=TokenFor(id);
            state.Hosts.Add(new(){Id=id,InstallationId=request.InstallationId,Name=request.Name,NetworkType=request.NetworkType,TokenHash=J.Hash(token),EnrollmentHash=hash,EnrollmentExpiry=expiry});
            state.PairingCodes.Remove(hash);Save();return new(id,token,config.HubId);
        }
    }
    public bool Revoke(string id){lock(gate){var host=state.Hosts.FirstOrDefault(h=>h.Id==id);if(host==null)return false;host.Revoked=true;Save();return true;}}
    public static string ConnectionState(long received,long sampled,long now)=>received==0||now-received>30?"Offline":Math.Abs(now-sampled)>10?"Stale":"Live";
    HostSummary Summary(RegisteredHost h)=>new(h.Id,h.Name,h.Snapshot?.Platform??"Waiting",h.NetworkType,h.ReceivedAt,h.Snapshot?.SampledAt??0,h.Revoked?"Revoked":ConnectionState(h.ReceivedAt,h.Snapshot?.SampledAt??0,J.Now),h.Snapshot?.QueueCount??0,h.Snapshot?.QueueBytes??0);
    public List<HostSummary> Hosts(){lock(gate)return state.Hosts.Select(Summary).ToList();}
    public void Live(RegisteredHost host,HostSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        lock(gate){if(host.Revoked)throw new UnauthorizedAccessException();host.Snapshot=snapshot;host.ReceivedAt=J.Now;Save();}
    }
    public static void ValidateSnapshot(HostSnapshot s)
    {
        if(s.SampledAt>J.Now+300||s.SampledAt<0||s.Readings.Count>5000||s.Models.Count>1000||s.Installed.Count>5000||s.Inventory.Count>5000||s.Processes.Count>1000||s.AgentApps.Sum(a=>a.Processes.Count)>10000||s.ModelStatus.Count>100||s.ActiveRequests.Count>1000||s.ActiveRequests.Values.Any(n=>n<0||n>100000)||s.LogicalProcessors is <1 or >65536)throw new InvalidDataException("Invalid snapshot bounds");
        if(s.SampledAt>0)Storage.ValidateEvent(new(1,"sample",JsonSerializer.Serialize(new SamplePayload(s.SampledAt,s.Readings))));
        // Re-serialization size also bounds nested names and arrays before persistence/querying.
        if(JsonSerializer.SerializeToUtf8Bytes(s).Length>1_500_000)throw new InvalidDataException("Snapshot too large");
    }
    public IngestAck Ingest(RegisteredHost host,IngestBatch batch)
    {
        if(batch.InstallationId!=host.InstallationId||batch.Events.Count>1000||batch.Events.Sum(e=>e.Payload.Length)>1_000_000)throw new InvalidDataException("Invalid batch");
        lock(gate){if(host.Revoked)throw new UnauthorizedAccessException();Store(host).ApplyBatch(host.InstallationId,batch.Events,true);return new(config.HubId,batch.Events.Select(e=>e.Sequence).ToList());}
    }
    public HostView? View(string id,long after,bool liveOnly=false)
    {
        lock(gate){var h=state.Hosts.FirstOrDefault(h=>h.Id==id);if(h==null)return null;var store=Store(h);var range=store.JournalRange();return new(config.HubId,Summary(h),h.Snapshot is {} s?s with{RetentionDays=config.RetentionDays}:null,liveOnly?[]:store.Events(after),range.First,range.Latest);}
    }
    public BaselineTicket BeginBaseline(string hostId)
    {
        lock(gate)
        {
            var host=state.Hosts.FirstOrDefault(h=>h.Id==hostId)??throw new InvalidDataException("Unknown host");
            foreach(var expiredId in baselines.Where(p=>p.Value.Created<J.Now-86400).Select(p=>p.Key).ToArray())ReleaseBaseline(baselines[expiredId].Host,expiredId);
            if(baselines.Values.Count(p=>p.Host==hostId)>=2)throw new InvalidOperationException("Two history transfers already active for this host");
            string id=Guid.NewGuid().ToString("N"),file=Path.Combine(root,"baseline-transfers",id+".db");long sequence=Store(host).ReplicaSnapshot(file);baselines[id]=(hostId,file,J.Now,new Storage(file,config.RetentionDays));return new(id,sequence,config.RetentionDays);
        }
    }
    public BaselinePage ReadBaseline(string hostId,string id,BaselineCursor cursor)
    {
        lock(gate)
        {
            if(!baselines.TryGetValue(id,out var source)||source.Host!=hostId||source.Created<J.Now-86400)throw new InvalidDataException("History transfer expired");
            return source.Reader.Baseline(cursor);
        }
    }
    public void ReleaseBaseline(string hostId,string id)
    {
        lock(gate)
        {
            if(!baselines.TryGetValue(id,out var source)||source.Host!=hostId)return;
            source.Reader.Dispose();foreach(var file in new[]{source.File,source.File+"-wal",source.File+"-shm"})File.Delete(file);baselines.Remove(id);
        }
    }
    public void Maintain()
    {
        lock(gate)
        {
            foreach(var id in baselines.Where(p=>p.Value.Created<J.Now-86400).Select(p=>p.Key).ToArray())ReleaseBaseline(baselines[id].Host,id);
            foreach(var h in state.Hosts){var store=Store(h);store.Retain(J.Now);store.RetainJournal(J.Now);store.Backup(Path.Combine(root,"hosts",h.Id,"backups"));}
        }
    }
    public async Task Run(CancellationToken ct)
    {
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],ContentRootPath=root});builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>{k.Limits.MaxRequestBodySize=2*1024*1024;k.Limits.MaxConcurrentConnections=64;k.Listen(IPAddress.Parse(config.ListenAddress),config.Port,l=>l.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root,config.CertificateFile),null)));});
        var app=builder.Build();using var slots=new SemaphoreSlim(8);
        static string Token(HttpContext c){string v=c.Request.Headers.Authorization.ToString();return v.StartsWith("Bearer ",StringComparison.Ordinal)?v[7..]:"";}
        app.Use(async(context,next)=>
        {
            if(!await slots.WaitAsync(0,ct)){context.Response.StatusCode=429;return;}
            try
            {
                if(context.Request.Path!="/fleet/v1/enroll")
                {
                    string token=Token(context);bool admin=IsAdmin(token);var host=admin?null:AuthenticateHost(token);
                    bool ingest=context.Request.Path is var path&&(path=="/fleet/v1/live"||path=="/fleet/v1/ingest");
                    if(ingest?host==null:!admin){context.Response.StatusCode=401;return;}
                    if(host!=null)context.Items["host"]=host;
                }
                else lock(gate)
                {
                    string ip=context.Connection.RemoteIpAddress?.ToString()??"unknown";long now=J.Now;
                    foreach(var key in enrollmentRates.Where(p=>now-p.Value.Start>60).Select(p=>p.Key).ToArray())enrollmentRates.Remove(key);
                    var rate=enrollmentRates.GetValueOrDefault(ip,(now,0));if(rate.Item2>=10||enrollmentRates.Count>1024){context.Response.StatusCode=429;return;}enrollmentRates[ip]=(rate.Item1,rate.Item2+1);
                }
                await next(context);
            }
            catch(Exception e) when(e is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {if(!context.Response.HasStarted){context.Response.StatusCode=e is UnauthorizedAccessException?401:400;await context.Response.WriteAsJsonAsync(new{error="Request rejected. Check identity, schema, size and clock."},ct);}}
            finally{slots.Release();}
        });
        app.MapPost("/fleet/v1/baseline/{id}",(string id)=>BeginBaseline(id));
        app.MapPost("/fleet/v1/baseline/{host}/{ticket}/page",(string host,string ticket,BaselineCursor cursor)=>ReadBaseline(host,ticket,cursor));
        app.MapPost("/fleet/v1/baseline/{host}/{ticket}/release",(string host,string ticket)=>{ReleaseBaseline(host,ticket);return Results.Ok();});
        app.MapPost("/fleet/v1/pair",()=>Pair());
        app.MapPost("/fleet/v1/enroll",(Enrollment r)=>Enroll(r));
        app.MapGet("/fleet/v1/hosts",()=>Hosts());
        app.MapGet("/fleet/v1/state/{id}",(string id)=>View(id,0,true) is {} view?Results.Ok(view):Results.NotFound());
        app.MapGet("/fleet/v1/hosts/{id}",(string id,long? after)=>View(id,Math.Max(0,after??0)) is {} view?Results.Ok(view):Results.NotFound());
        app.MapPost("/fleet/v1/revoke/{id}",(string id)=>Revoke(id)?Results.Ok():Results.NotFound());
        app.MapPost("/fleet/v1/live",(HttpContext c,HostSnapshot s)=>{Live((RegisteredHost)c.Items["host"]!,s);return Results.Ok();});
        app.MapPost("/fleet/v1/ingest",(HttpContext c,IngestBatch b)=>Ingest((RegisteredHost)c.Items["host"]!,b));
        var maintenance=Task.Run(async()=>{while(!ct.IsCancellationRequested){try{await Task.Delay(TimeSpan.FromHours(1),ct);Maintain();}catch(OperationCanceledException){}catch(Exception e){Paths.Log("hub-maintenance",e);}}},ct);
        try{await app.RunAsync(ct);}finally{await app.DisposeAsync();}
    }
    public void Dispose(){lock(gate){foreach(var s in stores.Values)s.Dispose();foreach(var source in baselines.Values)source.Reader.Dispose();}}
}
