using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiPulse;

public static class UsageFrameworkTests
{
    static int Port(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return port;}
    static KeyValue Attr(string key,string value)=>new(){Key=key,Value=new(){StringValue=value}};
    public static void Run(string root,Action<bool,string> check)=>RunAsync(root,check).GetAwaiter().GetResult();
    static async Task RunAsync(string root,Action<bool,string> check)
    {
        using var db=new Storage(Path.Combine(root,"framework.db"),replication:true);
        long now=J.Now;ulong nano=(ulong)now*1_000_000_000;
        await using var receiver=new TelemetryReceiver(db,Port());
        var log=new LogRecord{TimeUnixNano=nano,EventName="claude_code.api_request",Body=new(){StringValue="SECRET_FRAMEWORK_PROMPT"}};
        log.Attributes.AddRange(new[]{Attr("request_id","request-one"),Attr("model","claude-test"),Attr("session.id","session-one"),Attr("input_tokens","10"),Attr("output_tokens","7"),Attr("cache_read_tokens","100"),Attr("cache_creation_tokens","20"),Attr("prompt","SECRET_FRAMEWORK_PROMPT")});
        var logs=new ExportLogsServiceRequest();var resource=new ResourceLogs();var scope=new ScopeLogs();scope.LogRecords.Add(log);resource.ScopeLogs.Add(scope);logs.ResourceLogs.Add(resource);
        receiver.Process(logs);receiver.Process(logs);
        string fixture="""{"type":"assistant","sessionId":"session-one","requestId":"request-one","message":{"id":"message-one","model":"claude-test","usage":{"input_tokens":10,"output_tokens":7,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}} """;
        using(var doc=JsonDocument.Parse(fixture))AgentUsage.Parse(doc.RootElement,false,new(),"fallback",db);
        var span=new Span{Name="claude_code.llm_request",StartTimeUnixNano=nano-100_000_000,EndTimeUnixNano=nano,TraceId=ByteString.CopyFrom(new byte[16]),SpanId=ByteString.CopyFrom(new byte[8])};span.Attributes.AddRange(log.Attributes);
        var traces=new ExportTraceServiceRequest();var rs=new ResourceSpans();var ss=new ScopeSpans();ss.Spans.Add(span);rs.ScopeSpans.Add(ss);traces.ResourceSpans.Add(rs);receiver.Process(traces);
        var totals=db.TokenBreakdown(now-1);check(totals.Single().Input==130&&totals.Single().Output==7&&totals.Single().Records==1,"Claude native logs, spans and session records reconcile one request; cache subsets counted once");
        var counterId="pod-one:input";
        check(!db.ObserveCounter(counterId,"vLLM counters","pod-one","model","input",now-100,now-20,1000,true),"First cumulative server sample establishes baseline rather than charging lifetime tokens");
        db.ObserveCounter(counterId,"vLLM counters","pod-one","model","input",now-100,now-10,1200,true);
        db.ObserveCounter(counterId,"vLLM counters","pod-one","model","input",now-100,now-10,1200,true);
        db.ObserveCounter(counterId,"vLLM counters","pod-one","model","input",now-100,now-15,1100,true);
        db.ObserveCounter(counterId,"vLLM counters","pod-one","model","input",now-5,now-4,20,true);
        db.ObserveCounter(counterId,"vLLM counters","pod-one","model","input",now-5,now-1,40,true);
        check(db.CounterTotals(now-30,now).Single().Tokens==220&&db.Totals(now-1).Input==130,"Counter resets, replay and out-of-order batches are safe and never inflate request totals");
        db.ObserveCounter("delta","GenAI counters","instance","m","output",now-20,now-10,5,false);
        db.ObserveCounter("delta","GenAI counters","instance","m","output",now-20,now-10,5,false);
        db.ObserveCounter("delta","GenAI counters","instance","m","output",now-15,now-5,9,false);
        check(db.CounterTotals(now-30,now).Single(c=>c.Source=="GenAI counters").Tokens==5,"Delta metrics reject duplicate and overlapping intervals");
        db.ObserveCounter("precision","vLLM counters","pod-two","m","output",now-100,now,100,true,now*1_000_000_000+1,(now-100)*1_000_000_000);
        db.ObserveCounter("precision","vLLM counters","pod-two","m","output",now-100,now,103,true,now*1_000_000_000+2,(now-100)*1_000_000_000);
        check(db.CounterTotals(now-1,now).Single(c=>c.Instance=="pod-two").Tokens==3,"Nanosecond counter identity preserves distinct exports inside one second");
        check(db.CounterTotals(now-9,now).All(c=>c.Instance!="pod-one"||c.Tokens==20),"Selected calendar windows include whole intervals and do not guess a cross-boundary split");
        using(var replica=new Storage(Path.Combine(root,"framework-replica.db")))
        {var events=db.Events();replica.ApplyBatch("test",events,false);replica.ApplyBatch("test",events,false);check(replica.CounterTotals(now-30,now).Sum(c=>c.Tokens)==228&&replica.TokenBreakdown(now-1).Single().Records==1,"Fleet replay delivers new accounting types without duplicate counters or requests");}
        var native=new InferenceUsageObserver();byte[] nativeBytes=Encoding.UTF8.GetBytes("{\"response\":\"secret\",\"done\":false}\n{\"done\":true,\"prompt_eval_count\":12,\"prompt_eval_cached_count\":4,\"eval_count\":5,\"eval_duration\":1000000000}\n");
        foreach(byte b in nativeBytes)native.Feed(new[]{b});native.Finish();check(native.Input==12&&native.Output==5&&native.Cached==4&&native.Rate==5,"Native Ollama usage survives byte-split streaming and reads final provider counts");
        var responses=new InferenceUsageObserver();responses.Feed(Encoding.UTF8.GetBytes("event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":22,\"output_tokens\":6,\"input_tokens_details\":{\"cached_tokens\":3}}}}\n\ndata: [DONE]\n"));responses.Finish();check(responses.Input==22&&responses.Output==6&&responses.Cached==3,"OpenAI Responses SSE usage maps inclusive input and cache subsets correctly");
        int upstreamPort=Port(),gatewayPort=Port(),otlpPort=Port();
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel(o=>o.Listen(IPAddress.Loopback,upstreamPort));await using var upstream=builder.Build();
        bool requestedUsage=false;
        upstream.MapPost("/v1/chat/completions",async(HttpContext c)=>{using var json=await JsonDocument.ParseAsync(c.Request.Body);requestedUsage=json.RootElement.Get("stream_options").Get("include_usage").ValueKind==JsonValueKind.True;c.Response.ContentType="text/event-stream";await c.Response.WriteAsync("data: {\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":8}}\n\ndata: [DONE]\n\n");});
        upstream.MapPost("/api/pull",async(HttpContext c)=>{c.Response.StatusCode=201;await c.Response.WriteAsync("{\"status\":\"success\"}\n");});
        await upstream.StartAsync();using var ollama=new Ollama();await using var gateway=new Gateway(db,ollama,new(){GatewayPort=gatewayPort,OllamaUrl="http://127.0.0.1:"+upstreamPort},fullPassthrough:true);
        await gateway.Start(CancellationToken.None);using var client=new HttpClient();
        var health=await client.GetAsync("http://127.0.0.1:"+gatewayPort+"/health");check(health.IsSuccessStatusCode,"Gateway health endpoint is reachable before the forwarding fallback");
        var answer=await client.PostAsync("http://127.0.0.1:"+gatewayPort+"/v1/chat/completions",new StringContent("{\"model\":\"test\",\"stream\":true}",Encoding.UTF8,"application/json"));
        check(requestedUsage&&(await answer.Content.ReadAsStringAsync()).Contains("[DONE]")&&db.TokenBreakdown(now-1).Single(c=>c.Source=="Ollama").Total==25,"Gateway requests streaming usage and forwards bytes while recording provider totals");
        var pull=await client.PostAsync("http://127.0.0.1:"+gatewayPort+"/api/pull",new StringContent("{\"model\":\"test\"}",Encoding.UTF8,"application/json"));check((int)pull.StatusCode==201&&db.TokenBreakdown(now-1).Single(c=>c.Source=="Ollama").Records==1,"Persistent capture forwards model-management requests without counting them as inference");
        await using var httpReceiver=new TelemetryReceiver(db,otlpPort);await httpReceiver.Start(CancellationToken.None);
        var pb=new ByteArrayContent(logs.ToByteArray());pb.Headers.ContentType=new("application/x-protobuf");var ack=await client.PostAsync("http://127.0.0.1:"+otlpPort+"/v1/logs",pb);
        string traceJson=JsonFormatter.Default.Format(traces).Replace(Convert.ToBase64String(new byte[16]),new string('0',32)).Replace(Convert.ToBase64String(new byte[8]),new string('0',16));
        var jsonAck=await client.PostAsync("http://127.0.0.1:"+otlpPort+"/v1/traces",new StringContent(traceJson,Encoding.UTF8,"application/json"));
        check(ack.IsSuccessStatusCode&&jsonAck.IsSuccessStatusCode&&db.TokenBreakdown(now-1).Single(c=>c.Source=="Claude Code").Records==1,"Official OTLP protobuf and hex-ID JSON exports interoperate and deduplicate");
        using var browserRequest=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:"+otlpPort+"/v1/logs"){Content=new StringContent("{}",Encoding.UTF8,"application/json")};browserRequest.Headers.Add("Origin","https://example.com");var forbidden=await client.SendAsync(browserRequest);check(forbidden.StatusCode==HttpStatusCode.Forbidden,"Telemetry receiver rejects browser-origin exports");
        await upstream.StopAsync();check(db.Integrity()=="ok","Accounting storage remains structurally healthy after replay and network ingestion");
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(root,"framework.db"))){connection.Open();using var query=connection.CreateCommand();query.CommandText="SELECT sqlite_version()";check(Version.Parse((string)query.ExecuteScalar()!)>=new Version(3,51,3),"Bundled SQLite includes the official WAL-reset race fix");}
        // Force a verified consistent snapshot for the content-privacy assertion.
        var snapshot=Path.Combine(root,"framework-privacy.db");db.ReplicaSnapshot(snapshot);check(!Encoding.UTF8.GetString(File.ReadAllBytes(snapshot)).Contains("SECRET_FRAMEWORK_PROMPT"),"Neither prompt attributes nor OTLP log bodies are persisted");
        db.PutUsage(new("parallel-span","Ollama (OTLP)","m","",J.Now,1000,50,0,0,0,null,null,"Reported"));
        check(db.Totals(now-1).Input+db.Totals(now-1).Output==162&&db.CompactActivity(now-1,J.Now).Tokens==162,"Independent application spans are excluded from primary and compact totals to avoid gateway overlap");
        using(var coworkDb=new Storage(Path.Combine(root,"cowork.db")))
        {
            await using var coworkReceiver=new TelemetryReceiver(coworkDb,0);
            var cr=new ExportLogsServiceRequest();var resourceLog=new ResourceLogs{Resource=new Resource()};resourceLog.Resource.Attributes.Add(Attr("service.name","cowork"));var cs=new ScopeLogs();
            var requestLog=log.Clone();requestLog.EventName="api_request";requestLog.Attributes.Clear();requestLog.Attributes.AddRange(new[]{Attr("session.id","cowork-session"),Attr("event.sequence","12"),Attr("event.timestamp",DateTimeOffset.FromUnixTimeSeconds(now).ToString("O")),Attr("model","claude-test"),Attr("input_tokens","10"),Attr("output_tokens","5"),Attr("cache_read_tokens","100"),Attr("cache_creation_tokens","20")});cs.LogRecords.Add(requestLog);resourceLog.ScopeLogs.Add(cs);cr.ResourceLogs.Add(resourceLog);
            coworkReceiver.Process(cr);coworkReceiver.Process(cr);
            var row=coworkDb.TokenBreakdown(now-1).Single();check(row.Source=="Claude Cowork"&&row.Records==1&&row.Input==130&&row.Output==5,"Official Cowork api_request schema uses service and stable session/sequence identity, not prompt counts");
        }
    }
}
