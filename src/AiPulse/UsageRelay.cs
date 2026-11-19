using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiPulse;

public sealed class UsageRelayConfig
{
    public int ListenPort{get;set;}=11434;
    public int BackendPort{get;set;}=11436;
    public string OllamaExecutable{get;set;}="";
    public bool LaunchBackend{get;set;}
    public string ReadKey{get;set;}=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
public record RelayBatch(string Stream,List<SyncEvent> Events,long FirstSequence,long LatestSequence);
public record RelayBackfill(BaselineCursor Cursor,long ThroughSequence);

// A separate, unprivileged process owns the canonical Ollama port and durable metadata
// ledger. It never shares a writable pulse.db with the dashboard or stores request bodies.
public static class UsageRelay
{
    public static string ConfigFile=>Path.Combine(Paths.Root,"usage-relay.json");
    public static UsageRelayConfig? Load()=>File.Exists(ConfigFile)?JsonSerializer.Deserialize<UsageRelayConfig>(File.ReadAllText(ConfigFile)):null;
    public static int? ConfiguredPort()
    {
        try{return Load()?.ListenPort;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){Paths.Log("relay-config",e);return null;}
    }
    public static int Run()
    {
        var config=Load()??throw new InvalidOperationException("Create usage-relay.json before starting the relay.");
        if(config.ListenPort is <1024 or >65535||config.BackendPort is <1024 or >65535||config.ListenPort==config.BackendPort||config.ReadKey.Length!=64)throw new InvalidDataException("Invalid local relay configuration.");
        FileStream lockFile;
        // The watchdog task launches the relay every few minutes; an already-running relay is success, not a fault.
        try{lockFile=new FileStream(Path.Combine(Paths.Root,"usage-relay.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException)when(Answers(config.ListenPort,"/health")){return 0;}
        using var instance=lockFile;
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        using var db=new Storage(Path.Combine(Paths.Root,"relay","usage.db"),365,replication:true,durable:true);db.QueueBudgetBytes=256L*1024*1024;
        using var ollama=new Ollama();var settings=new AppSettings{EnableGateway=true,GatewayPort=config.ListenPort,OllamaUrl="http://127.0.0.1:"+config.BackendPort};
        var gateway=new Gateway(db,ollama,settings,fullPassthrough:true);
        bool Auth(HttpContext c)
        {
            string supplied=c.Request.Headers["X-AIPULSE-Read-Key"].ToString();
            return !c.Request.Headers.ContainsKey("Origin")&&supplied.Length==64&&CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(supplied),System.Text.Encoding.ASCII.GetBytes(config.ReadKey));
        }
        RelayBackendSupervisor? supervisor=null;
        gateway.Configure=app=>
        {
            app.Lifetime.ApplicationStopping.Register(stop.Cancel);
            app.MapGet("/aipulse/metadata",(HttpContext c)=>{if(!Auth(c))return Results.StatusCode(403);c.Response.Headers[BackendHeader]=supervisor?.State??(config.LaunchBackend?"Starting":"External");c.Response.Headers[InFlightHeader]=JsonSerializer.Serialize(gateway.InFlight.Values.OrderBy(r=>r.StartMs).Take(50));var range=db.JournalRange();return Results.Json(new RelayBatch(db.StoreId,db.Events(),range.First,range.Latest));});
            app.MapPost("/aipulse/baseline",async(HttpContext c)=>{if(!Auth(c))return Results.StatusCode(403);var cursor=await c.Request.ReadFromJsonAsync<BaselineCursor>();return Results.Json(db.Baseline(cursor??new()));});
            app.MapPost("/aipulse/ack",async(HttpContext c)=>
            {if(!Auth(c))return Results.StatusCode(403);var ids=await c.Request.ReadFromJsonAsync<List<long>>();if(ids==null||ids.Count>1000)return Results.BadRequest();db.Acknowledge(ids);return Results.Ok();});
        };
        try
        {
            // Bind first: never launch a backend if the canonical port belongs to somebody else.
            gateway.Start(stop.Token).GetAwaiter().GetResult();
            // An Ollama update can restore its own startup entry, so its server wins 11434 at sign-in.
            // With managed capture enabled the user already chose relay ownership of that port; reclaim it once.
            if(!gateway.Status.StartsWith("Listening")&&config.LaunchBackend&&ReclaimCanonicalPort(config))gateway.Start(stop.Token).GetAwaiter().GetResult();
            if(!gateway.Status.StartsWith("Listening")){Paths.Note("relay-start",$"Port {config.ListenPort} is held by another program; capture is off until it is released.");return 2;}
            if(config.LaunchBackend)
            {
                if(!Path.IsPathFullyQualified(config.OllamaExecutable)||!File.Exists(config.OllamaExecutable))throw new InvalidDataException("Use the installed Ollama executable's absolute path.");
                var backendLog=new BackendLogWriter(OllamaServerLog.RelayBackendLog);
                supervisor=new RelayBackendSupervisor(()=>Answers(config.BackendPort,"/api/version"),()=>
                {
                    var psi=new ProcessStartInfo(config.OllamaExecutable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};psi.ArgumentList.Add("serve");psi.Environment["OLLAMA_HOST"]="127.0.0.1:"+config.BackendPort;
                    var process=Process.Start(psi);if(process==null)return null;backendLog.Attach(process);return new ProcessRelayBackend(process);
                });
            }
            long maintain=0;
            while(!stop.IsCancellationRequested)
            {
                supervisor?.Tick(DateTime.UtcNow);
                if(J.Now-maintain>3600){db.Retain(J.Now);maintain=J.Now;}
                Thread.Sleep(1000);
            }
            return 0;
        }
        finally
        {
            gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
            supervisor?.Dispose();
        }
    }
    public const string BackendHeader="X-AIPULSE-Backend";
    public const string InFlightHeader="X-AIPULSE-InFlight";
    static readonly HttpClient probe=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(1.5)};
    internal static bool Answers(int port,string path)
    {
        try{using var response=probe.GetAsync("http://127.0.0.1:"+port+path).GetAwaiter().GetResult();return response.IsSuccessStatusCode;}
        catch(Exception e)when(e is HttpRequestException or TaskCanceledException){return false;}
    }
    // Stops only `ollama serve` processes of the configured executable (never the tray app or other programs)
    // and only when an Ollama server, not another AI Pulse gateway, is answering on the canonical port.
    static bool ReclaimCanonicalPort(UsageRelayConfig config)
    {
        if(!Path.IsPathFullyQualified(config.OllamaExecutable)||!Answers(config.ListenPort,"/api/version")||Answers(config.ListenPort,"/health"))return false;
        string target=Path.GetFullPath(config.OllamaExecutable);int stopped=0;
        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            using(process)
            {
                try{if(string.Equals(process.MainModule?.FileName,target,StringComparison.OrdinalIgnoreCase)){process.Kill(entireProcessTree:true);process.WaitForExit(5000);stopped++;}}
                catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){Paths.Log("relay-reclaim",e);}
            }
        if(stopped==0)return false;
        for(int i=0;i<20&&Answers(config.ListenPort,"/api/version");i++)Thread.Sleep(250);
        Paths.Note("relay-reclaim",$"Reclaimed port {config.ListenPort} from {stopped} Ollama server process(es).");
        return true;
    }
}

public sealed class UsageRelayClient(Storage db) : IDisposable
{
    readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(4),MaxResponseContentBufferSize=2*1024*1024};
    public List<InFlightRequest> InFlight {get;private set;}=[];
    public async Task Poll(CancellationToken ct)
    {
        try
        {
            var config=UsageRelay.Load();if(config==null)return;
            using var request=new HttpRequestMessage(HttpMethod.Get,"http://127.0.0.1:"+config.ListenPort+"/aipulse/metadata");request.Headers.Add("X-AIPULSE-Read-Key",config.ReadKey);
            using var response=await client.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength>1_000_000)throw new InvalidDataException();
            await using var stream=await response.Content.ReadAsStreamAsync(ct);using var limited=new MemoryStream();byte[] buffer=new byte[16384];int n;
            while((n=await stream.ReadAsync(buffer,ct))>0){if(limited.Length+n>1_000_000)throw new InvalidDataException();limited.Write(buffer,0,n);}
            var batch=JsonSerializer.Deserialize<RelayBatch>(limited.ToArray(),JsonSerializerOptions.Web)??throw new InvalidDataException();if(batch.Stream?.Length!=32||batch.Events==null||batch.Events.Count>1000)throw new InvalidDataException();
            string streamKey="relay:"+batch.Stream,progressKey="relay-backfill:"+batch.Stream;
            long.TryParse(db.SyncState("applied:"+streamKey),out long applied);
            string progress=db.SyncState(progressKey);
            if(progress!=""||db.SyncState("relay-seeded:"+batch.Stream)!="yes"||applied<batch.FirstSequence-1)
            {
                var backfill=progress!=""?JsonSerializer.Deserialize<RelayBackfill>(progress)!:new(new(),batch.LatestSequence);
                for(int page=0;page<2;page++)
                {
                    using var query=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:"+config.ListenPort+"/aipulse/baseline"){Content=JsonContent.Create(backfill.Cursor)};query.Headers.Add("X-AIPULSE-Read-Key",config.ReadKey);
                    using var result=await client.SendAsync(query,ct);result.EnsureSuccessStatusCode();
                    var data=await result.Content.ReadFromJsonAsync<BaselinePage>(cancellationToken:ct)??throw new InvalidDataException();
                    db.ApplyBatch("relay-baseline:"+batch.Stream+":"+backfill.ThroughSequence,data.Events,db.Replication);
                    if(data.Complete)
                    {db.SyncState("applied:"+streamKey,backfill.ThroughSequence.ToString());db.SyncState("relay-seeded:"+batch.Stream,"yes");db.SyncState(progressKey,"");break;}
                    backfill=backfill with{Cursor=data.Next};db.SyncState(progressKey,JsonSerializer.Serialize(backfill));
                    if(page==1){db.SourceState("Persistent Ollama capture","Backfilling","Recovering retained relay history in bounded pages. Pending events have not been acknowledged.");return;}
                }
            }
            db.ApplyBatch(streamKey,batch.Events,db.Replication);
            using var ack=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:"+config.ListenPort+"/aipulse/ack"){Content=JsonContent.Create(batch.Events.Select(e=>e.Sequence).ToList())};ack.Headers.Add("X-AIPULSE-Read-Key",config.ReadKey);
            using var accepted=await client.SendAsync(ack,ct);accepted.EnsureSuccessStatusCode();
            string backend=response.Headers.TryGetValues(UsageRelay.BackendHeader,out var values)?values.FirstOrDefault()??"":"";
            try{InFlight=response.Headers.TryGetValues(UsageRelay.InFlightHeader,out var flight)?JsonSerializer.Deserialize<List<InFlightRequest>>(flight.First())?.Where(r=>AgentLabel.Clean(r.Agent)==r.Agent||r.Agent=="").Take(50).ToList()??[]:[];}catch(JsonException){InFlight=[];}
            if(backend is "" or "Running" or "Adopted" or "External")
                db.SourceState("Persistent Ollama capture","Connected","Default Ollama port is instrumented. Metadata survives closing the dashboard; backend-port calls still bypass it.",batch.Events.Count>0?J.Now:0);
            else
                db.SourceState("Persistent Ollama capture","Degraded","Relay is up but its Ollama backend is not serving ("+backend+"). Requests fail until it restarts; the relay retries automatically.",batch.Events.Count>0?J.Now:0);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception e){InFlight=[];Paths.Log("relay-import",e);UsageRelayConfig? config=null;try{config=UsageRelay.Load();}catch(Exception){}if(config!=null)db.SourceState("Persistent Ollama capture","Disconnected",await Task.Run(()=>DiagnoseOffline(config)));}
    }
    // Explains why the relay is unreachable so the health card can offer the right repair.
    internal static string DiagnoseOffline(UsageRelayConfig config)
    {
        if(UsageRelay.Answers(config.ListenPort,"/api/version")&&!UsageRelay.Answers(config.ListenPort,"/health"))
            return $"Capture is OFF: Ollama's own server holds port {config.ListenPort} (an Ollama update usually restores its startup entry). Repair re-runs Enable-OllamaCapture.ps1; until then Ollama calls are not counted.";
        if(UsageRelay.Answers(config.BackendPort,"/api/version"))
            return $"Capture is OFF: the relay is not running but its backend on {config.BackendPort} is. The scheduled relay task restarts it within 5 minutes, or use Repair.";
        return $"Capture is OFF: nothing answers on port {config.ListenPort}. The relay and Ollama are both stopped; the scheduled relay task restarts them within 5 minutes, or use Repair.";
    }
    public void Dispose()=>client.Dispose();
}
