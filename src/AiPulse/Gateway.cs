using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPulse;

public sealed class Gateway(Storage db,Ollama ollama,AppSettings settings,bool fullPassthrough=false,int? relayPort=null) : IAsyncDisposable
{
    const string ObservationHeader="X-AIPULSE-Observation";
    public Action<WebApplication>? Configure {get;set;}
    WebApplication? app;
    readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
    public string Status{get;private set;}="Disabled";
    public async Task Start(CancellationToken ct)
    {
        if(!settings.EnableGateway||Status.StartsWith("Listening"))return;
        if(app!=null){await app.DisposeAsync();app=null;}
        try
        {
            var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel(o=>{o.Listen(IPAddress.Loopback,settings.GatewayPort);o.Limits.MaxRequestBodySize=fullPassthrough?null:16*1024*1024;});
            app=builder.Build();app.MapGet("/health",()=>Results.Json(new{service="AI Pulse observation gateway",version=typeof(Gateway).Assembly.GetName().Version?.ToString(3),store=db.StoreId}));
            Configure?.Invoke(app);app.MapFallback("/{**path}",Forward);await app.StartAsync(ct);Status="Listening on 127.0.0.1:"+settings.GatewayPort;
            db.SourceState("Ollama gateway","Ready","Requests through this address are observed. When the persistent relay handles a request, it records the usage once.");
        }catch(Exception e){if(app!=null){try{await app.DisposeAsync();}catch{}app=null;}Paths.Log("gateway-start",e);Status="Port unavailable; direct Ollama remains usable";db.SourceState("Ollama gateway","Error",Status);}
    }
    async Task Forward(HttpContext context)
    {
        if(context.Request.Headers.TryGetValue("Origin",out var origin)&&origin.Count>0&&origin.ToString()!=$"http://127.0.0.1:{settings.GatewayPort}"&&origin.ToString()!=$"http://localhost:{settings.GatewayPort}"){context.Response.StatusCode=403;return;}
        string path=context.Request.Path.Value??"";
        var inference=new[]{"/api/chat","/api/generate","/api/embed","/api/embeddings","/v1/chat/completions","/v1/completions","/v1/embeddings","/v1/responses","/v1/messages"};
        bool generation=context.Request.Method=="POST"&&inference.Contains(path);
        var allowed=inference.Concat(new[]{"/api/tags","/api/ps","/api/show","/api/version","/v1/models"});
        if(!fullPassthrough&&(!allowed.Contains(path)||context.Request.Method is not ("GET" or "POST"))){context.Response.StatusCode=404;return;}
        if(generation&&!(context.Request.ContentType??"").StartsWith("application/json",StringComparison.OrdinalIgnoreCase)){context.Response.StatusCode=415;return;}
        string model="",id=Guid.NewGuid().ToString("N"),outcome="Usage unavailable";long start=J.Now;
        var watch=System.Diagnostics.Stopwatch.StartNew();var usage=new InferenceUsageObserver();bool active=false,observe=true;
        try
        {
            using var request=new HttpRequestMessage(new HttpMethod(context.Request.Method),Ollama.Validate(settings.OllamaUrl)+path+context.Request.QueryString);
            if(generation)
            {
                using var buffer=new MemoryStream();byte[] bytes=new byte[16384];int n;
                while((n=await context.Request.Body.ReadAsync(bytes,context.RequestAborted))>0){if(buffer.Length+n>16*1024*1024){context.Response.StatusCode=413;return;}buffer.Write(bytes,0,n);}
                byte[] body=buffer.ToArray();
                try
                {
                    var node=JsonNode.Parse(body);model=node?["model"]?.GetValue<string>()??"";
                    if(path is "/v1/chat/completions" or "/v1/completions"&&node?["stream"]?.GetValue<bool>()==true)
                    {var options=node["stream_options"] as JsonObject??new();options["include_usage"]=true;node["stream_options"]=options;body=System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());}
                }catch(Exception e)when(e is JsonException or InvalidOperationException){}
                request.Content=new ByteArrayContent(body);
                ollama.Active.AddOrUpdate(model,1,(_,n)=>n+1);active=true;
            }
            else if(context.Request.ContentLength>0||context.Request.Headers.ContainsKey("Transfer-Encoding"))request.Content=new StreamContent(context.Request.Body);
            if(request.Content!=null)request.Content.Headers.TryAddWithoutValidation("Content-Type",context.Request.ContentType??"application/json");
            foreach(string name in new[]{"Range","If-None-Match","Accept"})if(context.Request.Headers.TryGetValue(name,out var h))request.Headers.TryAddWithoutValidation(name,h.ToArray());
            // Configured Ollama only. Never forward client account credentials or cookies.
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
            // Only the configured loopback relay can take ownership from this gateway.
            // If plain Ollama holds the same port, the desktop gateway still records.
            if(generation&&request.RequestUri is {IsLoopback:true,Scheme:"http"} upstream&&upstream.Port==relayPort&&
               response.Headers.TryGetValues(ObservationHeader,out var markers)&&markers.Contains("relay"))observe=false;
            if(generation&&fullPassthrough)context.Response.Headers[ObservationHeader]="relay";
            context.Response.StatusCode=(int)response.StatusCode;context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            foreach(string name in new[]{"Content-Range","ETag","Location"})if(response.Headers.TryGetValues(name,out var v)||response.Content.Headers.TryGetValues(name,out v))context.Response.Headers[name]=v.ToArray();
            outcome=response.IsSuccessStatusCode?"Usage unavailable":"HTTP "+(int)response.StatusCode;
            using var incoming=await response.Content.ReadAsStreamAsync(context.RequestAborted);var chunk=new byte[16384];int read;
            while((read=await incoming.ReadAsync(chunk,context.RequestAborted))>0)
            {
                if(generation&&observe)usage.Feed(chunk.AsSpan(0,read));
                await context.Response.Body.WriteAsync(chunk.AsMemory(0,read),context.RequestAborted);await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            if(generation&&observe){usage.Finish();if(response.IsSuccessStatusCode&&usage.HasInput&&(usage.HasOutput||path.Contains("embed")))outcome="Completed";else if(usage.HasInput||usage.HasOutput)outcome="Partial usage";}
        }
        catch(OperationCanceledException){outcome="Cancelled · usage incomplete";}
        catch(Exception e){Paths.Log("gateway-request",e);outcome="Failed · usage unavailable";if(!context.Response.HasStarted){context.Response.StatusCode=502;await context.Response.WriteAsJsonAsync(new{error="AI Pulse could not reach the configured Ollama server."});}}
        finally
        {
            if(active)ollama.Active.AddOrUpdate(model,0,(_,n)=>Math.Max(0,n-1));
            if(generation&&observe&&active)
            {
                db.PutUsage(new("ollama:"+id,"Ollama",usage.Model==""?model:usage.Model,"",start,usage.Input,usage.Output,usage.Cached,usage.CacheWrite,usage.Reasoning,watch.Elapsed.TotalMilliseconds,usage.Rate,outcome));
                db.SourceState("Ollama gateway",outcome=="Completed"?"Receiving":"Incomplete",outcome=="Completed"?"Provider-reported request counts captured. Unrouted requests remain invisible.":outcome,J.Now);
            }
        }
    }
    public async ValueTask DisposeAsync(){if(app!=null){await app.StopAsync();await app.DisposeAsync();}client.Dispose();}
}
