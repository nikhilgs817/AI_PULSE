using System.Text.Json;

namespace AiPulse;
public static class SelfTests
{
    public static int Run()
    {
        var results=new List<string>();var root=Path.Combine(Paths.Root,"test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Check(bool condition,string name){if(!condition)throw new Exception("FAILED: "+name);results.Add("PASS: "+name);}
        try
        {
            using(var db=new Storage(Path.Combine(root,"test.db")))
            {
                var now=J.Now;db.Sample(now,[new("cpu","CPU","test","%",20,"test")]);db.Sample(now,[new("cpu","CPU","test","%",99,"test")]);db.Sample(now+1,[new("cpu","CPU","test","%",40,"test")]);
                var history=db.History("cpu",now/60*60);Check(history.Sum(p=>p.Average)>0&&history.Max(p=>p.Maximum)==40,"Duplicate hardware samples ignored; maxima preserved");
                var usage=new Usage("test","Ollama","test-model","s",now,100,20,50,0,5,2000,10,"Completed");db.PutUsage(usage);db.PutUsage(usage);Check(db.Totals(now-1).Input==100&&db.Totals(now-1).Output==20,"Usage replay deduplicated; cache/reasoning are subsets");
                var state=new AgentUsage.ParseState{Session="codex-test",Model="test"};string fixture="""{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":10,"reasoning_output_tokens":3}}}}""";
                using(var j=JsonDocument.Parse(fixture)){AgentUsage.Parse(j.RootElement,true,state,"test",db);AgentUsage.Parse(j.RootElement,true,state,"test",db);}Check(db.Totals(now-1).Input==200,"Codex repeated cumulative counts are not double-counted");
                using(var j=JsonDocument.Parse("""{"type":"assistant","sessionId":"claude-test","requestId":"r1","message":{"id":"m1","model":"test","usage":{"input_tokens":10,"cache_read_input_tokens":50,"cache_creation_input_tokens":5,"output_tokens":12},"content":[{"text":"SECRET_PROMPT_SHOULD_NOT_PERSIST"}]}}""")){AgentUsage.Parse(j.RootElement,false,new(),"fallback",db);AgentUsage.Parse(j.RootElement,false,new(),"fallback",db);}Check(db.Totals(now-1).Input==265,"Claude cached input normalized and duplicate message deduplicated");
                db.Sample(now-8*86400,[new("old","old","test","%",30,"test")]);db.Sample(now-91*86400,[new("expired","expired","test","%",30,"test")]);db.Retain(now);Check(db.History("old",now-9*86400).Count>0&&db.History("expired",now-92*86400).Count==0,"90-day history retention boundary");
                var backup=db.Backup(Path.Combine(root,"backups"));Storage.VerifyBackup(backup);File.Copy(backup,Path.Combine(root,"restore.db"));using var restored=new Storage(Path.Combine(root,"restore.db"));Check(restored.Totals(now-1).Input==265,"Verified snapshot restores exact token totals");
                for(int i=0;i<20001;i++)db.PutUsage(new("bulk-"+i,"Test","=unsafe-formula","bulk",now,1,0,0,0,0,null,null,"Completed"));
                var export=Path.Combine(root,"export.csv");db.ExportUsage(export,now-1);var lines=File.ReadLines(export);Check(lines.Count()==20005&&lines.Any(l=>l.Contains("'=unsafe-formula")),"CSV exports beyond 20,000 records and escapes spreadsheet formulas");
            }
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"test.db"))).Contains("SECRET_PROMPT_SHOULD_NOT_PERSIST"),"Prompt bodies not persisted");
            try{Ollama.Validate("file:///etc/passwd");Check(false,"Invalid endpoint rejection");}catch(InvalidOperationException){results.Add("PASS: Invalid endpoint rejected");}
            File.WriteAllLines(Path.Combine(Paths.Root,"self-test-results.txt"),results);return 0;
        }
        catch(Exception e){results.Add(e.ToString());File.WriteAllLines(Path.Combine(Paths.Root,"self-test-results.txt"),results);return 1;}
    }
}
