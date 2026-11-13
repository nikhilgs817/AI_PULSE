using System.Text;
using System.Text.Json;

namespace AiPulse;

public sealed class AgentUsage(Storage db,List<string>? configuredRoots=null,MetadataAccess? access=null)
{
    public string Status {get;private set;}="Discovering local usage records";
    DateTime nextScan=DateTime.MinValue;
    List<string> files=[];
    int nextFile;
    int readErrors;
    int parseErrors;
    readonly object importGate=new();
    const string SpawnImportVersion="agent-spawn-import-v1";
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
        if(db.SyncState(SpawnImportVersion)!="yes")
        {
            db.ResetImportCheckpoints();
            db.SyncState(SpawnImportVersion,"yes");
        }
        if(DateTime.UtcNow>=nextScan)
        {
            var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots=configuredRoots?.Count>0?configuredRoots.ToArray():DefaultRoots(home);
            files=ReadUser(()=>
            {
                var found=new List<string>();
                foreach(var root in roots)try{if(Directory.Exists(root))found.AddRange(Directory.EnumerateFiles(root,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}).Where(f=>File.GetLastWriteTimeUtc(f)>DateTime.UtcNow.AddDays(-db.RetentionDays)));}catch{}
                return found.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            });nextScan=DateTime.UtcNow.AddSeconds(30);
        }
        // Revisit hottest files each pass and rotate the backlog. Keep work bounded so sampling stays responsive.
        var selected=files.Take(4).Concat(files.Skip(nextFile).Take(4)).Distinct().ToArray();nextFile=files.Count==0?0:(nextFile+4)%files.Count;
        readErrors=0;parseErrors=0;
        foreach(var file in selected)try{ReadNew(file);MarkRecentFile(file);}catch(MetadataUserUnavailableException){throw;}catch(Exception e){readErrors++;Paths.Log("usage-reader",e);}
        Status=files.Count==0?"No supported local session records found":"Local usage records • "+files.Count+" files discovered • "+db.RetentionDays+"-day import";
        if(readErrors>0)Status+=$" · {readErrors} files unreadable";
        if(parseErrors>0)Status+=$" · {parseErrors} malformed records skipped";
        foreach(string source in new[]{"Codex","Claude Code"})
        {
            var c=db.TokenCoverage(J.Now).FirstOrDefault(c=>c.Source==source);
            db.SourceState(source,readErrors>0||parseErrors>0?"Read errors":c==null?"No observations":J.Now-c.LastSeen>3600?"No recent records":"Receiving",source=="Claude Code"?"Session JSONL metadata only. Desktop Chat/Cowork may not expose exact token counts. "+Status:Status,c?.LastSeen??0);
        }
        db.SourceState("Claude desktop Chat","No token ledger","Chat usage is not exposed by this local session importer. Subscription percentages are not token counts.");
        if(!db.SourceStates().Any(s=>s.Source=="Claude Cowork"&&s.LastObserved>0))db.SourceState("Claude Cowork","Needs exporter","Desktop/cloud Cowork is separate from Code. Official OTLP export requires eligible Team/Enterprise admin setup; local process activity is not token usage.");
    }
    public static string[] DefaultRoots(string home)
    {
        var roots=new List<string>{Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(home,".codex"),"sessions"),Path.Combine(home,".claude","projects"),Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")??Path.Combine(home,".config"),"claude","projects")};
        foreach(var path in (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))roots.Add(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))=="projects"?path:Path.Combine(path,"projects"));
        return roots.Distinct(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).ToArray();
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
            try{using var doc=JsonDocument.Parse(bytes.AsMemory(start,i-start));Parse(doc.RootElement,codex,state,J.Hash(file),db);}catch(JsonException){parseErrors++;}
            start=i+1;
        }
        db.Checkpoint(file,offset+last+1,JsonSerializer.Serialize(state));
    }
    void MarkRecentFile(string file)
    {
        // Token history can be many megabytes behind after startup. File activity is a safe,
        // metadata-only heartbeat, so surface the live session while the bounded replay catches up.
        var modified=ReadUser(()=>File.GetLastWriteTimeUtc(file));
        var age=DateTime.UtcNow-modified;if(age<TimeSpan.FromSeconds(-60)||age>TimeSpan.FromMinutes(5))return;
        var state=JsonSerializer.Deserialize<ParseState>(db.Checkpoint(file).State);if(state==null||state.Session=="")return;
        bool codex=file.Contains(Path.DirectorySeparatorChar+"sessions"+Path.DirectorySeparatorChar)&&!file.Contains(Path.DirectorySeparatorChar+".claude"+Path.DirectorySeparatorChar);
        db.Session(state.Session,codex?"Codex":"Claude Code",state.Model,"Recent activity",new DateTimeOffset(modified).ToUnixTimeSeconds());
    }
    public static void Parse(JsonElement j,bool codex,ParseState s,string fallback,Storage db)
    {
        long time=J.Timestamp(j);string type=j.Str("type");
        if(codex)
        {
            var p=j.Get("payload");
            if(type=="session_meta")
            {
                s.Session=p.Str("id",fallback);
                var child=p.Get("source").Get("subagent").Get("thread_spawn");
                if(child.ValueKind==JsonValueKind.Object&&child.Str("parent_thread_id")!=""&&Guid.TryParse(p.Str("id"),out var childId))
                    db.RecordAgentSpawn("Codex",childId.ToString("D"),SpawnTime(p,time));
                return;
            }
            if(s.Session=="")s.Session=fallback;
            if(type=="turn_context"){s.Model=p.Str("model",s.Model);return;}
            if(type!="event_msg")return;
            string ev=p.Str("type");
            if(ev=="collab_agent_spawn_end"&&Guid.TryParse(p.Str("new_thread_id"),out var spawned))
                db.RecordAgentSpawn("Codex",spawned.ToString("D"),time);
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
            string claudeAgent=j.Str("agentId");
            if(j.Get("isSidechain").ValueKind==JsonValueKind.True&&j.Str("sessionId")!=""&&
               claudeAgent.Length is >0 and <=80&&claudeAgent.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))
                db.RecordAgentSpawn("Claude Code",j.Str("sessionId")+":"+claudeAgent,time);
            if(type!="assistant")return;
            var message=j.Get("message");var u=message.Get("usage");if(u.ValueKind!=JsonValueKind.Object)return;
            bool hasInput=u.Get("input_tokens").ValueKind==JsonValueKind.Number,hasOutput=u.Get("output_tokens").ValueKind==JsonValueKind.Number;
            if(!hasInput&&!hasOutput)return;
            string model=message.Str("model",s.Model);s.Model=model;
            string requestId=j.Str("requestId");string id=j.Str("requestId",message.Str("id",j.Str("uuid")));
            if(id=="")return;
            long cache=u.Num("cache_read_input_tokens"),write=CacheWrites(u),input=u.Num("input_tokens")+cache+write;
            string legacy="claude:"+s.Session+":"+id+":"+message.Str("id");
            db.PutClaudeUsage(new(legacy,"Claude Code",model,s.Session,time,input,u.Num("output_tokens"),cache,write,u.Get("output_tokens_details").Num("thinking_tokens"),null,null,hasInput&&hasOutput?"Reported":"Partial usage"),requestId,legacy);
            // Recent Claude Code logs can include separately billed advisor iterations.
            // Main message iterations are already included above and must not be added again.
            if(u.Get("iterations").ValueKind==JsonValueKind.Array)
            {
                int index=0;foreach(var iteration in u.Get("iterations").EnumerateArray().Take(1000))
                {
                    if(iteration.Str("type")!="advisor_message"||iteration.Str("model")=="")continue;
                    long advisorCache=iteration.Num("cache_read_input_tokens"),advisorWrite=CacheWrites(iteration);
                    string advisorId="claude-advisor:"+J.Hash(s.Session+":"+id+":"+message.Str("id")+":"+index++);
                    db.PutUsage(new(advisorId,"Claude Code",iteration.Str("model"),s.Session,time,iteration.Num("input_tokens")+advisorCache+advisorWrite,iteration.Num("output_tokens"),advisorCache,advisorWrite,0,null,null,"Reported"));
                }
            }
            db.Session(s.Session,"Claude Code",model,"Recent activity",time);
        }
    }
    static long SpawnTime(JsonElement metadata,long fallback)=>DateTimeOffset.TryParse(metadata.Str("timestamp"),out var start)?start.ToUnixTimeSeconds():fallback;
    static long CacheWrites(JsonElement usage)=>usage.Get("cache_creation_input_tokens").ValueKind==JsonValueKind.Number?usage.Num("cache_creation_input_tokens"):usage.Get("cache_creation").Num("ephemeral_5m_input_tokens")+usage.Get("cache_creation").Num("ephemeral_1h_input_tokens");
}
