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
            Check(J.SensorValue(-316368.19,"%")==null&&J.SensorValue(101,"%")==null&&J.SensorValue(double.NaN,"°C")==null&&J.SensorValue(32,"°C")==32&&J.SensorValue(0,"%")==0,"Impossible sensor values are unavailable; valid idle and temperature readings survive");
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
                var split=db.TokenBreakdown(now-1);Check(split.Sum(r=>r.Input)==db.Totals(now-1).Input&&split.Sum(r=>r.Records)==20004&&split.Single(r=>r.Source=="Test").Input==20001,"Token aggregates cover all records beyond the 20,000-row display limit");
                var local=split.Single(r=>r.OnPrem);var claude=split.Single(r=>r.Source=="Claude Code");Check(local.Total==120&&claude.Input==65&&claude.Uncached==10&&claude.Cached==50&&claude.CacheWrite==5&&claude.Total==77,"On-prem and tool splits preserve cache subsets without double-counting totals");
            }
            using(var tokens=new Storage(Path.Combine(root,"tokens.db")))
            {
                long now=J.Now;tokens.PutUsage(new("old-token","Ollama","model","",now-3*86400,100,10,0,0,0,null,null,"Completed"));
                tokens.PutUsage(new("partial-token","Ollama","model","",now,5,2,0,0,0,null,null,"Cancelled · usage incomplete"));
                tokens.PutUsage(new("tool-token","Codex","model","session",now,10,8,3,0,4,null,null,"Reported"));
                var day=tokens.TokenBreakdown(now-86400);Check(day.Single(r=>r.OnPrem).Total==7&&day.Single(r=>r.OnPrem).Incomplete==1&&tokens.TokenBreakdown(now-7*86400).Single(r=>r.OnPrem).Total==117,"Token ranges separate recent history and flag incomplete observations");
                Check(day.Single(r=>!r.OnPrem).Reasoning==4&&day.Single(r=>!r.OnPrem).Total==18,"Reasoning stays a subset of output and same model names remain separated by source");
            }
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"test.db"))).Contains("SECRET_PROMPT_SHOULD_NOT_PERSIST"),"Prompt bodies not persisted");
            using(var compact=new Storage(Path.Combine(root,"compact.db")))
            {
                long now=J.Now,midnight=J.DayStart(DateTimeOffset.FromUnixTimeSeconds(now),TimeZoneInfo.Local);
                compact.PutUsage(new("yesterday","Ollama","m","",midnight-1,900,100,500,0,0,null,null,"Completed"));
                compact.PutUsage(new("today","Codex","m","",midnight,100,20,60,0,5,null,null,"Reported"));
                compact.PutUsage(new("future","Ollama","m","",now+120,800,100,0,0,0,null,null,"Completed"));
                for(int i=0;i<102;i++)compact.Session("active-"+i,"Codex","m","Working",now-10);
                compact.Session("recent","Claude Code","m","Recent activity",now-20);compact.Session("complete","Codex","m","Completed",now);compact.Session("interrupted","Codex","m","Interrupted",now);compact.Session("quiet","Codex","m","Working",now-301);compact.Session("future-session","Codex","m","Working",now+60);
                var summary=compact.CompactActivity(midnight,now);
                Check(summary.ActiveAgents==103,"Compact active count includes all recent sessions beyond the 100-row list and excludes quiet/completed/future sessions");
                Check(summary.Tokens==120&&summary.Tools==120&&summary.OnPrem==0,"Tokens today starts at local midnight, excludes future records and does not add cache/reasoning twice");
                var zone=TimeZoneInfo.CreateCustomTimeZone("test-offset",TimeSpan.FromHours(-5),"test","test");
                Check(J.DayStart(DateTimeOffset.Parse("2026-10-19T02:00:00Z"),zone)==DateTimeOffset.Parse("2026-10-18T05:00:00Z").ToUnixTimeSeconds(),"Local calendar-day boundary differs from UTC midnight and rolling 24 hours");
            }
            using(var year=new Storage(Path.Combine(root,"year.db"),365))
            {
                long now=J.Now,inside=now-364*86400L,outside=now-366*86400L;
                year.Sample(inside,[new("year","Year boundary","test","%",42,"test")]);year.Sample(outside,[new("expired-year","Expired","test","%",99,"test")]);
                year.PutUsage(new("year-usage","Test","old-model","old-session",inside,321,12,0,0,0,null,null,"Reported"));year.PutUsage(new("expired-year-usage","Test","old-model","old-session",outside,999,0,0,0,0,null,null,"Reported"));
                year.Retain(now);Check(year.History("year",outside).Count==1&&year.History("expired-year",outside-1).Count==0&&year.Totals(outside).Input==321,"365-day history and usage boundaries preserve in-range records only");
                var snapshot=year.Backup(Path.Combine(root,"year-backups"));Storage.VerifyBackup(snapshot);using(var restoredYear=new Storage(snapshot,365))Check(restoredYear.Totals(outside).Input==321,"Year-old usage survives verified backup and restore");
                year.Session("needle-session","Codex","historic-model","Completed",now-500);for(int i=0;i<110;i++)year.Session("newer-"+i,"Claude Code","other","Working",now-i);
                Check(year.Sessions(365).All(s=>s.Id!="needle-session")&&year.Sessions(365,"HISTORIC").Single().Id=="needle-session","Agent search filters before the 100-result limit, case-insensitively");
                year.Checkpoint("fixture",100,"{}");var importer=new AgentUsage(year);importer.SetRetention(90);Check(year.RetentionDays==90&&year.Checkpoint("fixture").Offset==100,"Reducing retention preserves importer checkpoints");
                importer.SetRetention(365);Check(year.Checkpoint("fixture").Offset==0,"Increasing retention schedules a deduplicated agent re-import");
                year.RetentionDays=90;year.Retain(now);Check(year.Totals(outside).Input==0&&year.History("year",outside).Count==0,"Reducing retention cleans older hardware and usage records");
                year.RetentionDays=500;Check(year.RetentionDays==365&&new AppSettings().RetentionDays==90,"Retention caps at one year and shipping default stays 90 days");
            }
            try{Ollama.Validate("file:///etc/passwd");Check(false,"Invalid endpoint rejection");}catch(InvalidOperationException){results.Add("PASS: Invalid endpoint rejected");}
            File.WriteAllLines(Path.Combine(Paths.Root,"self-test-results.txt"),results);return 0;
        }
        catch(Exception e){results.Add(e.ToString());File.WriteAllLines(Path.Combine(Paths.Root,"self-test-results.txt"),results);return 1;}
    }
}
