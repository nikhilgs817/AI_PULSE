using System.Text;
using System.Text.Json;

namespace AiPulse;

public sealed class AgentUsage(Storage db,List<string>? configuredRoots=null,MetadataAccess? access=null)
{
    public string Status {get;private set;}="Discovering local usage records";
    DateTime nextScan=DateTime.MinValue;
    List<string> files=[];
    int nextFile;
    readonly object importGate=new();
    public void SetRetention(int days)
    {
        lock(importGate){if(days>db.RetentionDays)db.ResetImportCheckpoints();db.RetentionDays=days;nextScan=DateTime.MinValue;nextFile=0;}
    }
    public sealed class ParseState
    {
        public string Session {get;set;}="";
        public string Model {get;set;}="";
        public long Input {get;set;}
        public long Output {get;set;}
        public long Cached {get;set;}
        public long Reasoning {get;set;}
        public long Segment {get;set;}
        public bool SkippingOversizedLine {get;set;}
    }
    T ReadUser<T>(Func<T> work)=>access==null?work():access.Read(work);
    public void Poll()
    {lock(importGate)try{PollCore();}catch(MetadataUserUnavailableException e){Status=e.Message;}}
    void PollCore()
    {
        if(DateTime.UtcNow>=nextScan)
        {
            var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots=configuredRoots?.Count>0?configuredRoots.ToArray():new[]{Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(home,".codex"),"sessions"),Path.Combine(home,".claude","projects")};
            files=ReadUser(()=>
            {
                var found=new List<string>();
                foreach(var root in roots)try{if(Directory.Exists(root))found.AddRange(Directory.EnumerateFiles(root,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}).Where(f=>File.GetLastWriteTimeUtc(f)>DateTime.UtcNow.AddDays(-db.RetentionDays)));}catch{}
                return found.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            });nextScan=DateTime.UtcNow.AddSeconds(30);
        }
        // Revisit hottest files each pass and rotate the backlog. Keep work bounded so sampling stays responsive.
        var selected=files.Take(4).Concat(files.Skip(nextFile).Take(4)).Distinct().ToArray();nextFile=files.Count==0?0:(nextFile+4)%files.Count;
        foreach(var file in selected)try{ReadNew(file);}catch(MetadataUserUnavailableException){throw;}catch(Exception e){Paths.Log("usage-reader",e);}
        Status=files.Count==0?"No supported local session records found":"Local usage records • "+files.Count+" files discovered • "+db.RetentionDays+"-day import";
    }
    void ReadNew(string file)
    {
        var cp=db.Checkpoint(file);var state=JsonSerializer.Deserialize<ParseState>(cp.State)??new();
        var block=ReadUser(()=>
        {
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            long position=cp.Offset>stream.Length?0:cp.Offset;int requested=(int)Math.Min(4*1024*1024,stream.Length-position);
            stream.Position=position;var buffer=new byte[requested];int count=requested>0?stream.Read(buffer):0;
            return (Offset:position,Bytes:buffer,Read:count,Length:requested,Reset:cp.Offset>stream.Length);
        });
        if(block.Reset)state=new();if(block.Read<=0)return;
        long offset=block.Offset;var bytes=block.Bytes;int read=block.Read,length=block.Length;int last=Array.LastIndexOf(bytes,(byte)'\n',read-1,read);
        if(last<0){if(length>=4*1024*1024){state.SkippingOversizedLine=true;db.Checkpoint(file,offset+read,JsonSerializer.Serialize(state));}return;}
        bool codex=file.Contains(Path.DirectorySeparatorChar+"sessions"+Path.DirectorySeparatorChar)&&!file.Contains(Path.DirectorySeparatorChar+".claude"+Path.DirectorySeparatorChar);
        int start=state.SkippingOversizedLine?Array.IndexOf(bytes,(byte)'\n',0,read)+1:0;state.SkippingOversizedLine=false;
        for(int i=start;i<=last;i++)if(bytes[i]==10)
        {
            // Only parse allowlisted metadata. Never persist raw JSON, prompts, responses or tool arguments.
            try{using var doc=JsonDocument.Parse(bytes.AsMemory(start,i-start));Parse(doc.RootElement,codex,state,J.Hash(file),db);}catch(JsonException){}
            start=i+1;
        }
        db.Checkpoint(file,offset+last+1,JsonSerializer.Serialize(state));
    }
    public static void Parse(JsonElement j,bool codex,ParseState s,string fallback,Storage db)
    {
        long time=J.Timestamp(j);string type=j.Str("type");
        if(codex)
        {
            var p=j.Get("payload");
            if(type=="session_meta"){s.Session=p.Str("id",fallback);return;}
            if(s.Session=="")s.Session=fallback;
            if(type=="turn_context"){s.Model=p.Str("model",s.Model);return;}
            if(type!="event_msg")return;
            string ev=p.Str("type");
            if(ev=="token_count")
            {
                var info=p.Get("info");var usage=info.Get("total_token_usage");if(usage.ValueKind!=JsonValueKind.Object)return;
                long input=usage.Num("input_tokens"),output=usage.Num("output_tokens"),cached=usage.Num("cached_input_tokens"),reasoning=usage.Num("reasoning_output_tokens");
                if(input<s.Input||output<s.Output){s.Input=s.Output=s.Cached=s.Reasoning=0;s.Segment++;}
                if(input>s.Input||output>s.Output)
                    db.PutUsage(new("codex:"+s.Session+":"+s.Segment+":"+input+":"+output,"Codex",s.Model,s.Session,time,input-s.Input,output-s.Output,Math.Max(0,cached-s.Cached),0,Math.Max(0,reasoning-s.Reasoning),null,null,"Reported"));
                s.Input=input;s.Output=output;s.Cached=cached;s.Reasoning=reasoning;
                db.Session(s.Session,"Codex",s.Model,"Recent activity",time);
            }
            else if(ev is "task_started" or "task_complete" or "turn_aborted")db.Session(s.Session,"Codex",s.Model,ev=="task_started"?"Working":ev=="task_complete"?"Completed":"Interrupted",time);
        }
        else
        {
            s.Session=j.Str("sessionId",s.Session==""?fallback:s.Session);
            if(type!="assistant")return;
            var message=j.Get("message");var u=message.Get("usage");if(u.ValueKind!=JsonValueKind.Object)return;
            string model=message.Str("model",s.Model);s.Model=model;
            string id=j.Str("requestId",message.Str("id",j.Str("uuid")));
            if(id=="")return;
            long cache=u.Num("cache_read_input_tokens"),write=u.Num("cache_creation_input_tokens"),input=u.Num("input_tokens")+cache+write;
            db.PutUsage(new("claude:"+s.Session+":"+id+":"+message.Str("id"),"Claude Code",model,s.Session,time,input,u.Num("output_tokens"),cache,write,u.Get("output_tokens_details").Num("thinking_tokens"),null,null,"Reported"));
            db.Session(s.Session,"Claude Code",model,"Recent activity",time);
        }
    }
}
