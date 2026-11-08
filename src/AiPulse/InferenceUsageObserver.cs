using System.Text;
using System.Text.Json;

namespace AiPulse;

// One state machine per request. Replace snapshots rather than summing final chunks.
public sealed class InferenceUsageObserver
{
    readonly MemoryStream line=new();bool discard;
    readonly MemoryStream whole=new();bool wholeTooLarge;
    public long Input{get;private set;}
    public long Output{get;private set;}
    public long Cached{get;private set;}
    public long CacheWrite{get;private set;}
    public long Reasoning{get;private set;}
    public string Model{get;private set;}="";
    public bool HasInput{get;private set;}
    public bool HasOutput{get;private set;}
    public bool Complete{get;private set;}
    public double? Rate{get;private set;}
    public void Feed(ReadOnlySpan<byte> bytes){if(!wholeTooLarge){if(whole.Length+bytes.Length<=2*1024*1024)whole.Write(bytes);else{wholeTooLarge=true;whole.SetLength(0);}}foreach(byte b in bytes){if(b==10)Inspect();else if(!discard){if(line.Length<2*1024*1024)line.WriteByte(b);else{discard=true;line.SetLength(0);}}}}
    public void Finish(){if(line.Length>0)Inspect();if(!wholeTooLarge&&whole.Length>0)try{using var doc=JsonDocument.Parse(whole.GetBuffer().AsMemory(0,(int)whole.Length));Observe(doc.RootElement);}catch(JsonException){} }
    void Inspect()
    {
        if(discard){line.SetLength(0);discard=false;return;}
        string text=Encoding.UTF8.GetString(line.GetBuffer(),0,(int)line.Length).Trim();line.SetLength(0);
        if(text.StartsWith("data:"))text=text[5..].Trim();if(text==""||text=="[DONE]"||text.StartsWith("event:"))return;
        try{using var doc=JsonDocument.Parse(text);Observe(doc.RootElement);}catch(JsonException){}
    }
    static long? Count(JsonElement j,string key){var v=j.Get(key);return v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out long n)&&n is >=0 and <=1_000_000_000_000?n:null;}
    public void Observe(JsonElement r)
    {
        if(r.Get("message").ValueKind==JsonValueKind.Object&&r.Str("type")=="message_start")r=r.Get("message");
        if(r.Get("response").ValueKind==JsonValueKind.Object&&r.Str("type").StartsWith("response."))r=r.Get("response");
        Model=r.Str("model",Model);
        if(Count(r,"prompt_eval_count") is long ni){Input=ni;HasInput=true;}
        if(Count(r,"eval_count") is long no){Output=no;HasOutput=true;}
        Cached=Count(r,"prompt_eval_cached_count")??Cached;
        if(r.Get("done").ValueKind==JsonValueKind.True)Complete=true;
        if(r.Num("eval_duration")>0)Rate=Output/(r.Num("eval_duration")/1e9);
        var u=r.Get("usage");if(u.ValueKind!=JsonValueKind.Object)return;
        bool anthropic=Count(u,"cache_read_input_tokens")!=null||Count(u,"cache_creation_input_tokens")!=null||r.Str("type") is "message" or "message_delta";
        long cache=Count(u,"cache_read_input_tokens")??Count(u.Get("prompt_tokens_details"),"cached_tokens")??Count(u.Get("input_tokens_details"),"cached_tokens")??Cached;
        long write=Count(u,"cache_creation_input_tokens")??CacheWrite;
        if((Count(u,"prompt_tokens")??Count(u,"input_tokens")) is long i){Input=i+(anthropic?cache+write:0);HasInput=true;}
        if((Count(u,"completion_tokens")??Count(u,"output_tokens")) is long o){Output=o;HasOutput=true;}
        Cached=cache;CacheWrite=write;Reasoning=Count(u.Get("completion_tokens_details"),"reasoning_tokens")??Count(u.Get("output_tokens_details"),"reasoning_tokens")??Reasoning;
        Complete=HasInput&&HasOutput;
    }
}
