using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AiPulse;

public record UsageSourceState(string Source,string State,string Detail,long Checked,long LastObserved);
public record TokenCounterInterval(string Id,string Source,string Instance,string Model,string Kind,long Start,long Time,long Tokens);
public record TokenCounterTotal(string Source,string Instance,string Model,string Kind,long Tokens,long First,long Last);
public record UsageReplacement(Usage Usage,string Alias){public long Time=>Usage.Time;}

public sealed partial class Storage
{
    void InitializeAccounting()=>Execute("""
        CREATE TABLE IF NOT EXISTS usage_sources(source TEXT PRIMARY KEY,state TEXT NOT NULL,detail TEXT NOT NULL,checked INTEGER NOT NULL,last_observed INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS token_counter_state(series TEXT PRIMARY KEY,start INTEGER NOT NULL,time INTEGER NOT NULL,value INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS token_intervals(id TEXT PRIMARY KEY,source TEXT NOT NULL,instance TEXT NOT NULL,model TEXT NOT NULL,kind TEXT NOT NULL,start INTEGER NOT NULL,time INTEGER NOT NULL,tokens INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS token_intervals_time ON token_intervals(time);
        CREATE TABLE IF NOT EXISTS accounting_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        INSERT OR IGNORE INTO accounting_meta VALUES('store_id',lower(hex(randomblob(16))));
        """);
    public string StoreId {get{lock(gate)return Convert.ToString(Scalar("SELECT value FROM accounting_meta WHERE key='store_id'"))??"";}}
    public void SourceState(string source,string state,string detail,long observed=0)
    {
        var value=new UsageSourceState(source,state,detail,J.Now,observed);
        lock(gate){using var tx=db.BeginTransaction();SourceStateCore(value);AppendEvent("usage-source",value);tx.Commit();}
    }
    void SourceStateCore(UsageSourceState s)=>Execute("INSERT INTO usage_sources VALUES(@p0,@p1,@p2,@p3,@p4) ON CONFLICT(source) DO UPDATE SET state=excluded.state,detail=excluded.detail,checked=excluded.checked,last_observed=max(last_observed,excluded.last_observed)",s.Source,s.State,s.Detail,s.Checked,s.LastObserved);
    public List<UsageSourceState> SourceStates()
    {
        lock(gate){using var c=Command("SELECT * FROM usage_sources ORDER BY source");using var r=c.ExecuteReader();var result=new List<UsageSourceState>();while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4)));return result;}
    }
    // A native request ID is stable across Claude session JSONL, OTLP logs and spans.
    // Remove only its explicit legacy alias, atomically with the replacement; never clear history.
    public void PutClaudeUsage(Usage usage,string requestId,string? legacyAlias=null)
    {
        if(string.IsNullOrWhiteSpace(requestId)){PutUsage(usage);return;}
        usage=usage with{Id="claude-request:"+J.Hash(requestId+"\n"+usage.Model)};
        if(usage.Time<J.Now-RetentionDays*86400L||usage.Time>J.Now+300)return;
        lock(gate){using var tx=db.BeginTransaction();
            if(!string.IsNullOrEmpty(legacyAlias))Execute("DELETE FROM usage WHERE id=@p0",legacyAlias);
            UsageCore(usage);AppendEvent("usage-replacement",new UsageReplacement(usage,legacyAlias??""));tx.Commit();}
    }
    // Server counters are a separate measurement boundary. Never add them to request totals.
    // First cumulative sample establishes a baseline; resets, replays and old batches do not
    // charge a server's lifetime consumption to today. Keep the exporter start timestamp.
    public bool ObserveCounter(string series,string source,string instance,string model,string kind,long start,long time,long value,bool cumulative,long preciseTime=0,long preciseStart=0)
    {
        if(value<0||value>1_000_000_000_000||time<=0||time>J.Now+300||time<J.Now-RetentionDays*86400L||start>time)return false;
        lock(gate){using var tx=db.BeginTransaction();
            long stamp=preciseTime>0?preciseTime:time*1_000_000_000,epoch=preciseStart>0?preciseStart:start*1_000_000_000;
            long oldStart=0,oldTime=0,oldValue=0;bool exists;
            using(var c=Command("SELECT start,time,value FROM token_counter_state WHERE series=@p0",series))using(var r=c.ExecuteReader()){exists=r.Read();if(exists){oldStart=r.GetInt64(0);oldTime=r.GetInt64(1);oldValue=r.GetInt64(2);}}
            if(exists&&stamp<=oldTime)return false;
            bool valid=cumulative?exists&&epoch==oldStart&&value>=oldValue: start>0&&(!exists||epoch>=oldTime);
            long count=cumulative?value-oldValue:value,from=cumulative?oldTime/1_000_000_000:start;
            Execute("INSERT INTO token_counter_state VALUES(@p0,@p1,@p2,@p3) ON CONFLICT(series) DO UPDATE SET start=excluded.start,time=excluded.time,value=excluded.value",series,epoch,stamp,value);
            if(valid&&count>=0){var interval=new TokenCounterInterval("counter:"+J.Hash(series+":"+stamp),source,instance,model,kind,from,time,count);CounterCore(interval);AppendEvent("token-interval",interval);}
            tx.Commit();return valid;
        }
    }
    void CounterCore(TokenCounterInterval i)=>Execute("INSERT OR IGNORE INTO token_intervals VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)",i.Id,i.Source,i.Instance,i.Model,i.Kind,i.Start,i.Time,i.Tokens);
    public List<TokenCounterTotal> CounterTotals(long since,long until)
    {
        // Whole observation intervals only. Do not invent a split for an interval crossing midnight.
        lock(gate){using var c=Command("SELECT source,instance,model,kind,sum(tokens),min(start),max(time) FROM token_intervals WHERE start>=@p0 AND time<=@p1 GROUP BY source,instance,model,kind",since,until);using var r=c.ExecuteReader();var rows=new List<TokenCounterTotal>();while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6)));return rows;}
    }
    public string Integrity()
    {lock(gate){using var c=Command("PRAGMA quick_check(1)");return Convert.ToString(c.ExecuteScalar())??"Check failed";}}
}
