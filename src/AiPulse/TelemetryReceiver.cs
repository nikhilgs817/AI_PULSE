using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;

namespace AiPulse;

// OTLP wire types are generated from the pinned, unmodified official protocol.
// Parse in memory; only allowlisted numerical usage/identity metadata reaches SQLite.
public sealed class TelemetryReceiver(Storage db,int port) : IAsyncDisposable
{
    WebApplication? app;
    public string Status{get;private set;}="Disabled";
    public async Task Start(CancellationToken ct)
    {
        if(port is <1 or >65535)return;
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o=>{o.Listen(IPAddress.Loopback,port);o.Limits.MaxRequestBodySize=8*1024*1024;o.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);});
        app=builder.Build();app.MapGet("/health",()=>Results.Json(new{service="AIPULSE OTLP/HTTP",store=db.StoreId}));
        app.MapPost("/v1/logs",c=>Receive(c,"logs"));app.MapPost("/v1/traces",c=>Receive(c,"traces"));app.MapPost("/v1/metrics",c=>Receive(c,"metrics"));
        try{await app.StartAsync(ct);Status="Listening on 127.0.0.1:"+port;db.SourceState("OTLP receiver","Ready","Waiting for configured exporters. No exporter traffic means no coverage.");}
        catch(Exception e){Paths.Log("otlp-start",e);Status="Receiver port unavailable";db.SourceState("OTLP receiver","Error",Status);await app.DisposeAsync();app=null;}
    }
    async Task Receive(HttpContext c,string signal)
    {
        if(c.Request.Headers.ContainsKey("Origin")){c.Response.StatusCode=403;return;}
        bool json=(c.Request.ContentType??"").Split(';')[0].Trim().Equals("application/json",StringComparison.OrdinalIgnoreCase);
        if(!json&&c.Request.ContentType!="application/x-protobuf"){c.Response.StatusCode=415;return;}
        string encoding=c.Request.Headers.ContentEncoding.ToString();
        if(encoding!=""&&encoding!="gzip"){c.Response.StatusCode=415;return;}
        try
        {
            using var unzipped=encoding=="gzip"?new GZipStream(c.Request.Body,CompressionMode.Decompress,true):null;
            var stream=(Stream?)unzipped??c.Request.Body;using var body=new MemoryStream();byte[] buffer=new byte[16384];int count;
            while((count=await stream.ReadAsync(buffer,c.RequestAborted))>0){if(body.Length+count>8*1024*1024){c.Response.StatusCode=413;return;}body.Write(buffer,0,count);}
            byte[] bytes=body.ToArray();string? text=json?NormalizeJson(System.Text.Encoding.UTF8.GetString(bytes)):null;
            var parser=new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));IMessage result;
            switch(signal)
            {
                case "logs":Process(json?parser.Parse<ExportLogsServiceRequest>(text!):ExportLogsServiceRequest.Parser.ParseFrom(bytes));result=new ExportLogsServiceResponse();break;
                case "traces":Process(json?parser.Parse<ExportTraceServiceRequest>(text!):ExportTraceServiceRequest.Parser.ParseFrom(bytes));result=new ExportTraceServiceResponse();break;
                default:Process(json?parser.Parse<ExportMetricsServiceRequest>(text!):ExportMetricsServiceRequest.Parser.ParseFrom(bytes));result=new ExportMetricsServiceResponse();break;
            }
            db.SourceState("OTLP receiver","Receiving","Accepted "+signal+" export. See each adapter's coverage; arbitrary logs are discarded.",J.Now);
            c.Response.ContentType=json?"application/json":"application/x-protobuf";
            await c.Response.Body.WriteAsync(json?System.Text.Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(result)):result.ToByteArray(),c.RequestAborted);
        }
        catch(Exception e)when(e is InvalidProtocolBufferException or System.Text.Json.JsonException or InvalidDataException or FormatException or OverflowException or ArgumentException)
        {c.Response.StatusCode=400;db.SourceState("OTLP receiver","Invalid export","Rejected malformed, unsupported or out-of-bounds telemetry. Raw payload not retained.");}
        catch(Exception e){Paths.Log("otlp-write",e);c.Response.StatusCode=503;db.SourceState("OTLP receiver","Storage error","Usage export was not acknowledged. Exporter should retry.");}
    }
    // OTLP JSON uses hex IDs, unlike the generic protobuf JSON bytes representation.
    static string NormalizeJson(string json)
    {
        var root=JsonNode.Parse(json,new JsonNodeOptions(),new System.Text.Json.JsonDocumentOptions{MaxDepth=64})??throw new InvalidDataException();
        void Visit(JsonNode? n)
        {
            if(n is JsonObject obj)foreach(var pair in obj.ToArray())
            {
                if(pair.Key is "traceId" or "spanId" or "parentSpanId"&&pair.Value is JsonValue v&&v.TryGetValue<string>(out var s)&&s.Length>0)
                {int length=pair.Key=="traceId"?32:16;if(s.Length!=length||!s.All(Uri.IsHexDigit))throw new InvalidDataException();obj[pair.Key]=Convert.ToBase64String(Convert.FromHexString(s));}
                else Visit(pair.Value);
            }
            else if(n is JsonArray array)foreach(var child in array)Visit(child);
        }
        Visit(root);return root.ToJsonString();
    }
    static readonly HashSet<string> Allowed=new(StringComparer.Ordinal)
    {
        "event.name","event.sequence","event.timestamp","model","input_tokens","output_tokens","cache_read_tokens","cache_creation_tokens","duration_ms","request_id","client_request_id","session.id","session_id",
        "gen_ai.provider.name","gen_ai.system","gen_ai.request.model","gen_ai.response.model","gen_ai.response.id","gen_ai.usage.input_tokens","gen_ai.usage.output_tokens",
        "gen_ai.usage.cache_read.input_tokens","gen_ai.usage.cache_write.input_tokens","gen_ai.usage.cache_creation.input_tokens","gen_ai.usage.reasoning.output_tokens",
        "service.name","service.instance.id","k8s.pod.uid","k8s.namespace.name","k8s.cluster.name","model_name","model.name","gen_ai.token.type","type","instance","job","engine","engine_id","source"
    };
    static Dictionary<string,string> Attributes(IEnumerable<KeyValue>? values)
    {
        var d=new Dictionary<string,string>(StringComparer.Ordinal);if(values==null)return d;
        foreach(var v in values.Take(512))if(Allowed.Contains(v.Key)&&v.Value!=null)
        {
            string? s=v.Value.ValueCase switch{AnyValue.ValueOneofCase.StringValue=>v.Value.StringValue,AnyValue.ValueOneofCase.IntValue=>v.Value.IntValue.ToString(CultureInfo.InvariantCulture),AnyValue.ValueOneofCase.DoubleValue=>v.Value.DoubleValue.ToString(CultureInfo.InvariantCulture),_=>null};
            if(s is {Length:<=512}&&!s.Any(char.IsControl))d[v.Key]=s;
        }
        return d;
    }
    static Dictionary<string,string> Merge(Dictionary<string,string> resource,IEnumerable<KeyValue> values){var d=new Dictionary<string,string>(resource);foreach(var p in Attributes(values))d[p.Key]=p.Value;return d;}
    static string S(Dictionary<string,string> d,params string[] names)=>names.Select(n=>d.GetValueOrDefault(n,"")).FirstOrDefault(v=>v!="")??"";
    static long? N(Dictionary<string,string> d,params string[] names){var s=S(d,names);return long.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out long n)&&n is >=0 and <=1_000_000_000_000?n:null;}
    static long Seconds(ulong nano)=>checked((long)(nano/1_000_000_000));
    static string Instance(Dictionary<string,string> a)=>S(a,"k8s.pod.uid","service.instance.id","instance");
    static string Model(Dictionary<string,string> a)=>S(a,"gen_ai.response.model","gen_ai.request.model","model_name","model.name","model");
    void Request(Dictionary<string,string> a,long time,string eventName,string trace,string span,double? duration=null)
    {
        bool cowork=S(a,"service.name")=="cowork"&&eventName is "api_request" or "claude_code.api_request";
        bool claude=cowork||eventName is "claude_code.api_request" or "claude_code.llm_request";
        if(!claude&&eventName.StartsWith("claude_code.",StringComparison.Ordinal))return;
        long? input=N(a,claude?"input_tokens":"gen_ai.usage.input_tokens"),output=N(a,claude?"output_tokens":"gen_ai.usage.output_tokens");
        if(input==null&&output==null)return;
        if(time<=0||time>J.Now+300||time<J.Now-db.RetentionDays*86400L)throw new InvalidDataException();
        long cache=N(a,claude?"cache_read_tokens":"gen_ai.usage.cache_read.input_tokens")??0,write=N(a,claude?"cache_creation_tokens":"gen_ai.usage.cache_write.input_tokens","gen_ai.usage.cache_creation.input_tokens")??0;
        long normalized=(input??0)+(claude?cache+write:0); // GenAI convention input already includes cache subsets.
        string provider=S(a,"gen_ai.provider.name","gen_ai.system"),request=S(a,"request_id","gen_ai.response.id","client_request_id"),model=Model(a),session=S(a,"session.id","session_id");
        string source=cowork?"Claude Cowork":claude?"Claude Code":provider.ToLowerInvariant() switch{"ollama"=>"Ollama (OTLP)","vllm"=>"vLLM (OTLP)",_=>"OTLP · "+(provider==""?S(a,"service.name"):provider)};
        if(cowork&&request==""&&session!=""&&S(a,"event.sequence")!="")request="cowork-event:"+session+":"+S(a,"event.sequence")+":"+S(a,"event.timestamp");
        string identity=request!=""?provider+":"+Instance(a)+":"+request:trace!=""&&span!=""?trace+":"+span:"";
        if(identity==""){db.SourceState(source,"Incomplete","Usage had no stable request or trace identity; not counted to avoid replay duplicates.");return;}
        var u=new Usage("otlp:"+J.Hash(identity),source,model,session,time,normalized,output??0,cache,write,N(a,"gen_ai.usage.reasoning.output_tokens")??0,duration??N(a,"duration_ms"),null,input!=null&&output!=null?"Reported":"Partial usage");
        if(claude)db.PutClaudeUsage(u,request);else db.PutUsage(u);
        if(session!="")db.Session(session,source,model,"Recent activity",time);
        db.SourceState(source,"Receiving","Request metadata received through OTLP. Content is discarded.",time);
    }
    static string Hex(ByteString bytes)=>Convert.ToHexString(bytes.Span);
    public void Process(ExportLogsServiceRequest request)
    {
        int n=0;foreach(var resource in request.ResourceLogs){var a=Attributes(resource.Resource?.Attributes);foreach(var scope in resource.ScopeLogs)foreach(var log in scope.LogRecords)
        {if(++n>10000)throw new InvalidDataException();var values=Merge(a,log.Attributes);string ev=log.EventName!=""?log.EventName:S(values,"event.name");if(ev==""&&log.Body?.ValueCase==AnyValue.ValueOneofCase.StringValue&&log.Body.StringValue is "claude_code.api_request" or "api_request")ev=log.Body.StringValue;if(ev!="claude_code.api_request"&&!(ev=="api_request"&&S(values,"service.name")=="cowork"))continue;long at=Seconds(log.TimeUnixNano!=0?log.TimeUnixNano:log.ObservedTimeUnixNano);if(at==0&&DateTimeOffset.TryParse(S(values,"event.timestamp"),out var parsed))at=parsed.ToUnixTimeSeconds();Request(values,at,ev,Hex(log.TraceId),Hex(log.SpanId));}}
    }
    public void Process(ExportTraceServiceRequest request)
    {
        int n=0;foreach(var resource in request.ResourceSpans){var a=Attributes(resource.Resource?.Attributes);foreach(var scope in resource.ScopeSpans)foreach(var span in scope.Spans)
        {if(++n>10000)throw new InvalidDataException();Request(Merge(a,span.Attributes),Seconds(span.EndTimeUnixNano),span.Name,Hex(span.TraceId),Hex(span.SpanId),span.EndTimeUnixNano>=span.StartTimeUnixNano?(span.EndTimeUnixNano-span.StartTimeUnixNano)/1e6:null);}}
    }
    public void Process(ExportMetricsServiceRequest request)
    {
        int n=0;foreach(var resource in request.ResourceMetrics){var a=Attributes(resource.Resource?.Attributes);foreach(var scope in resource.ScopeMetrics)foreach(var metric in scope.Metrics)
        {
            string name=metric.Name.Replace(":",".").Replace("_",".");
            bool claude=metric.Name=="claude_code.token.usage",genai=metric.Name=="gen_ai.client.token.usage";
            bool prompt=name is "vllm.prompt.tokens.total" or "vllm.prompt.tokens",output=name is "vllm.generation.tokens.total" or "vllm.generation.tokens";
            bool cached=name is "vllm.prompt.tokens.cached.total" or "vllm.prompt.tokens.cached",bySource=name is "vllm.prompt.tokens.by.source.total" or "vllm.prompt.tokens.by.source";
            bool vllm=prompt||output||cached||bySource;
            if(!claude&&!genai&&!vllm||metric.Sum==null||!metric.Sum.IsMonotonic)continue;
            bool cumulative=metric.Sum.AggregationTemporality==AggregationTemporality.Cumulative;
            if(!cumulative&&metric.Sum.AggregationTemporality!=AggregationTemporality.Delta)continue;
            foreach(var point in metric.Sum.DataPoints)
            {
                if(++n>10000)throw new InvalidDataException();if((point.Flags&1)!=0)continue;
                var attr=Merge(a,point.Attributes);string instance=Instance(attr),model=Model(attr);
                if(S(attr,"engine","engine_id") is string engine&&engine!="")instance+=" / engine "+engine;
                if(instance==""){db.SourceState("OTLP counters","Incomplete","Exporter must include service.instance.id, k8s.pod.uid or instance to distinguish restarts and replicas.");continue;}
                double number=point.ValueCase==NumberDataPoint.ValueOneofCase.AsInt?point.AsInt:point.AsDouble;
                if(!double.IsFinite(number)||number<0||number>1e12||number!=Math.Truncate(number))throw new InvalidDataException();
                string kind=prompt?"input":output?"output":cached?"cacheRead":bySource?"input."+S(attr,"source"):S(attr,"gen_ai.token.type","type");if(kind is not ("input" or "output" or "cacheRead" or "cacheCreation" or "input.local_compute" or "input.local_cache_hit" or "input.external_kv_transfer"))continue;
                string source=claude?"Claude Code counters":vllm?"vLLM counters":"GenAI counters";
                string series=J.Hash(metric.Name+":"+string.Join(";",attr.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value)));
                long at=Seconds(point.TimeUnixNano);
                db.ObserveCounter(series,source,instance,model,kind,Seconds(point.StartTimeUnixNano),at,(long)number,cumulative,checked((long)point.TimeUnixNano),checked((long)point.StartTimeUnixNano));
                db.SourceState(source,"Receiving","Separate interval totals; never added to request records. First cumulative sample is a baseline.",at);
            }
        }}
    }
    public async ValueTask DisposeAsync(){if(app!=null){await app.StopAsync();await app.DisposeAsync();}}
}
