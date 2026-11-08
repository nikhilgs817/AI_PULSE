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
    public static int Run()
    {
        var config=Load()??throw new InvalidOperationException("Create usage-relay.json before starting the relay.");
        if(config.ListenPort is <1024 or >65535||config.BackendPort is <1024 or >65535||config.ListenPort==config.BackendPort||config.ReadKey.Length!=64)throw new InvalidDataException("Invalid local relay configuration.");
        using var instance=new FileStream(Path.Combine(Paths.Root,"usage-relay.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        using var db=new Storage(Path.Combine(Paths.Root,"relay","usage.db"),365,replication:true,durable:true);db.QueueBudgetBytes=256L*1024*1024;
        using var ollama=new Ollama();var settings=new AppSettings{EnableGateway=true,GatewayPort=config.ListenPort,OllamaUrl="http://127.0.0.1:"+config.BackendPort};
        var gateway=new Gateway(db,ollama,settings,fullPassthrough:true);
        bool Auth(HttpContext c)
        {
            string supplied=c.Request.Headers["X-AIPULSE-Read-Key"].ToString();
            return !c.Request.Headers.ContainsKey("Origin")&&supplied.Length==64&&CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(supplied),System.Text.Encoding.ASCII.GetBytes(config.ReadKey));
        }
        gateway.Configure=app=>
        {
            app.MapGet("/aipulse/metadata",(HttpContext c)=>{if(!Auth(c))return Results.StatusCode(403);var range=db.JournalRange();return Results.Json(new RelayBatch(db.StoreId,db.Events(),range.First,range.Latest));});
            app.MapPost("/aipulse/baseline",async(HttpContext c)=>{if(!Auth(c))return Results.StatusCode(403);var cursor=await c.Request.ReadFromJsonAsync<BaselineCursor>();return Results.Json(db.Baseline(cursor??new()));});
            app.MapPost("/aipulse/ack",async(HttpContext c)=>
            {if(!Auth(c))return Results.StatusCode(403);var ids=await c.Request.ReadFromJsonAsync<List<long>>();if(ids==null||ids.Count>1000)return Results.BadRequest();db.Acknowledge(ids);return Results.Ok();});
        };
        Process? backend=null;
        try
        {
            // Bind first: never launch a backend if the canonical port belongs to somebody else.
            gateway.Start(stop.Token).GetAwaiter().GetResult();if(!gateway.Status.StartsWith("Listening"))return 2;
            if(config.LaunchBackend)
            {
                if(!Path.IsPathFullyQualified(config.OllamaExecutable)||!File.Exists(config.OllamaExecutable))throw new InvalidDataException("Use the installed Ollama executable's absolute path.");
                var psi=new ProcessStartInfo(config.OllamaExecutable){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add("serve");psi.Environment["OLLAMA_HOST"]="127.0.0.1:"+config.BackendPort;
                backend=Process.Start(psi)??throw new IOException("Could not start the configured Ollama backend.");
            }
            long maintain=0;
            while(!stop.IsCancellationRequested)
            {
                if(backend?.HasExited==true)throw new IOException("Managed Ollama backend exited; the relay will not silently run without its backend.");
                if(J.Now-maintain>3600){db.Retain(J.Now);maintain=J.Now;}
                Thread.Sleep(1000);
            }
            return 0;
        }
        finally
        {
            gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if(backend is {HasExited:false})backend.Kill(entireProcessTree:true);backend?.Dispose();
        }
    }
}

public sealed class UsageRelayClient(Storage db) : IDisposable
{
    readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(4),MaxResponseContentBufferSize=2*1024*1024};
    public async Task Poll(CancellationToken ct)
    {
        var config=UsageRelay.Load();if(config==null)return;
        try
        {
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
            db.SourceState("Persistent Ollama capture","Connected","Default Ollama port is instrumented. Metadata survives closing the dashboard; backend-port calls still bypass it.",batch.Events.Count>0?J.Now:0);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception e){Paths.Log("relay-import",e);db.SourceState("Persistent Ollama capture","Disconnected","Relay unavailable or delivery failed. Check its process, port and local configuration; history remains retained.");}
    }
    public void Dispose()=>client.Dispose();
}
