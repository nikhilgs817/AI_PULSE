using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AiPulse;

public sealed class Gateway(Storage db,Ollama ollama,AppSettings settings) : IAsyncDisposable
{
    WebApplication? app;
    readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
    public string Status{get;private set;}="Disabled";
    public async Task Start(CancellationToken ct)
    {
        if(!settings.EnableGateway||Status.StartsWith("Listening"))return;
        if(app!=null){await app.DisposeAsync();app=null;}
        try
        {
            var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel(o=>{o.Listen(IPAddress.Loopback,settings.GatewayPort);o.Limits.MaxRequestBodySize=16*1024*1024;});
            app=builder.Build();app.MapGet("/health",()=>Results.Json(new{service="AI Pulse observation gateway",version=typeof(Gateway).Assembly.GetName().Version?.ToString(3)}));
            app.Run(Forward);await app.StartAsync(ct);Status="Listening on 127.0.0.1:"+settings.GatewayPort;
        }catch(Exception e){if(app!=null){await app.DisposeAsync();app=null;}Paths.Log("gateway-start",e);Status="Port unavailable; direct Ollama remains usable";}
    }
    async Task Forward(HttpContext context)
    {
        if(context.Request.Headers.TryGetValue("Origin",out var origin)&&origin.Count>0&&origin.ToString()!=$"http://127.0.0.1:{settings.GatewayPort}"){context.Response.StatusCode=403;return;}
        if(context.Request.Method=="POST"&&!(context.Request.ContentType??"").StartsWith("application/json",StringComparison.OrdinalIgnoreCase)){context.Response.StatusCode=415;return;}
        string path=context.Request.Path.Value??"";
        var allowed=new[]{"/api/chat","/api/generate","/api/embed","/api/embeddings","/api/tags","/api/ps","/api/show","/api/version","/v1/chat/completions","/v1/completions","/v1/embeddings","/v1/models"};
        if(!allowed.Contains(path)||context.Request.Method is not ("GET" or "POST")){context.Response.StatusCode=404;return;}
        string model="",id=Guid.NewGuid().ToString("N");bool generation=path.Contains("chat")||path.Contains("generate")||path.Contains("completions")||path.Contains("embed");
        var watch=System.Diagnostics.Stopwatch.StartNew();long input=0,output=0,cache=0;double? rate=null;bool usage=false;string outcome="Usage unavailable";long start=J.Now;
        try
        {
            using var buffer=new MemoryStream();await context.Request.Body.CopyToAsync(buffer,context.RequestAborted);byte[] body=buffer.ToArray();
            if(body.Length>0){try{using var request=JsonDocument.Parse(body);model=request.RootElement.Str("model");}catch(JsonException){}}
            if(generation)ollama.Active.AddOrUpdate(model,1,(_,n)=>n+1);
            using var requestMessage=new HttpRequestMessage(new HttpMethod(context.Request.Method),Ollama.Validate(settings.OllamaUrl)+path+context.Request.QueryString);
            if(body.Length>0){requestMessage.Content=new ByteArrayContent(body);requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type",context.Request.ContentType??"application/json");}
            // The gateway is only for a configured Ollama server. Never forward cookies or account credentials.
            using var response=await client.SendAsync(requestMessage,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
            context.Response.StatusCode=(int)response.StatusCode;
            context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            outcome=response.IsSuccessStatusCode?"Usage unavailable":"HTTP "+(int)response.StatusCode;
            using var incoming=await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var chunk=new byte[16384];var line=new MemoryStream();bool discard=false;
            void Inspect()
            {
                if(discard){line.SetLength(0);discard=false;return;}
                var text=Encoding.UTF8.GetString(line.GetBuffer(),0,(int)line.Length).Trim();line.SetLength(0);
                if(text.StartsWith("data:"))text=text[5..].Trim();if(text==""||text=="[DONE]")return;
                try
                {
                    using var doc=JsonDocument.Parse(text);var r=doc.RootElement;
                    if(r.Get("eval_count").ValueKind==JsonValueKind.Number||r.Get("prompt_eval_count").ValueKind==JsonValueKind.Number)
                    {input=r.Num("prompt_eval_count");output=r.Num("eval_count");cache=r.Num("prompt_eval_cached_count");double duration=r.Num("eval_duration");rate=duration>0?output/(duration/1e9):null;usage=true;outcome="Completed";}
                    var u=r.Get("usage");if(u.ValueKind==JsonValueKind.Object){input=u.Num("prompt_tokens");output=u.Num("completion_tokens");cache=u.Get("prompt_tokens_details").Num("cached_tokens");usage=true;outcome="Completed";}
                }catch(JsonException){}
            }
            int read;while((read=await incoming.ReadAsync(chunk,context.RequestAborted))>0)
            {
                // Forward bytes unchanged, without waiting for complete JSON messages.
                await context.Response.Body.WriteAsync(chunk.AsMemory(0,read),context.RequestAborted);await context.Response.Body.FlushAsync(context.RequestAborted);
                for(int i=0;i<read;i++){if(chunk[i]==10)Inspect();else if(!discard){if(line.Length<2*1024*1024)line.WriteByte(chunk[i]);else{discard=true;line.SetLength(0);}}}
            }
            if(line.Length>0)Inspect();
        }
        catch(OperationCanceledException){outcome="Cancelled · usage incomplete";}
        catch(Exception e){Paths.Log("gateway-request",e);outcome="Failed · usage unavailable";if(!context.Response.HasStarted){context.Response.StatusCode=502;await context.Response.WriteAsJsonAsync(new{error="AI Pulse could not reach the configured Ollama server."});}}
        finally
        {
            if(generation)
            {
                ollama.Active.AddOrUpdate(model,0,(_,n)=>Math.Max(0,n-1));
                db.PutUsage(new("ollama:"+id,"Ollama",model,"",start,input,output,cache,0,0,watch.Elapsed.TotalMilliseconds,rate,usage?outcome:outcome));
            }
        }
    }
    public async ValueTask DisposeAsync(){if(app!=null){await app.StopAsync();await app.DisposeAsync();}client.Dispose();}
}
