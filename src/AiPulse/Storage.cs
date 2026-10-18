using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Security.Cryptography;

namespace AiPulse;

public sealed class Storage : IDisposable
{
    readonly object gate=new();
    readonly SqliteConnection db;
    int retentionDays=90;
    public int RetentionDays {get=>Volatile.Read(ref retentionDays);set=>Volatile.Write(ref retentionDays,Math.Clamp(value,7,365));}
    public Storage(string path,int retentionDays=90)
    {
        RetentionDays=retentionDays;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        var version=Convert.ToInt32(Scalar("PRAGMA user_version"));
        if(version>1) throw new InvalidOperationException("Database belongs to a newer AI Pulse version.");
        Execute("""
        CREATE TABLE IF NOT EXISTS metrics(time INTEGER NOT NULL,id TEXT NOT NULL,value REAL NOT NULL,PRIMARY KEY(time,id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS minute_metrics(time INTEGER NOT NULL,id TEXT NOT NULL,total REAL NOT NULL,count INTEGER NOT NULL,minimum REAL NOT NULL,maximum REAL NOT NULL,PRIMARY KEY(time,id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS minute_metrics_sensor_time ON minute_metrics(id,time);
        CREATE TABLE IF NOT EXISTS sensors(id TEXT PRIMARY KEY,name TEXT,device TEXT,unit TEXT,source TEXT);
        CREATE TABLE IF NOT EXISTS usage(id TEXT PRIMARY KEY,source TEXT,model TEXT,session TEXT,time INTEGER,input INTEGER,output INTEGER,cached INTEGER,cache_write INTEGER,reasoning INTEGER,duration REAL,rate REAL,status TEXT);
        CREATE INDEX IF NOT EXISTS usage_time ON usage(time);
        CREATE INDEX IF NOT EXISTS usage_session ON usage(session);
        CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,source TEXT,model TEXT,state TEXT,last_seen INTEGER);
        CREATE TABLE IF NOT EXISTS checkpoints(path TEXT PRIMARY KEY,offset INTEGER,state TEXT);
        CREATE TABLE IF NOT EXISTS alerts(key TEXT PRIMARY KEY,title TEXT,detail TEXT,time INTEGER,active INTEGER);
        PRAGMA user_version=1;
        """);
    }
    SqliteCommand Command(string sql,params object?[] values)
    {
        var c=db.CreateCommand();c.CommandText=sql;
        for(int i=0;i<values.Length;i++) c.Parameters.AddWithValue("@p"+i,values[i]??DBNull.Value);
        return c;
    }
    void Execute(string sql,params object?[] values) {using var c=Command(sql,values);c.ExecuteNonQuery();}
    object? Scalar(string sql,params object?[] values) {using var c=Command(sql,values);return c.ExecuteScalar();}
    public void Sample(long time,IEnumerable<Reading> readings)
    {
        lock(gate){ using var tx=db.BeginTransaction();
        foreach(var r in readings)
        {
            Execute("INSERT INTO sensors VALUES(@p0,@p1,@p2,@p3,@p4) ON CONFLICT(id) DO UPDATE SET name=excluded.name,device=excluded.device,unit=excluded.unit,source=excluded.source",r.Id,r.Name,r.Device,r.Unit,r.Source);
            if(r.Value is not double v||!double.IsFinite(v)) continue;
            using var c=Command("INSERT OR IGNORE INTO metrics VALUES(@p0,@p1,@p2)",time,r.Id,v);
            if(c.ExecuteNonQuery()==0)continue;
            Execute("INSERT INTO minute_metrics VALUES(@p0,@p1,@p2,1,@p2,@p2) ON CONFLICT(time,id) DO UPDATE SET total=total+excluded.total,count=count+1,minimum=min(minimum,excluded.minimum),maximum=max(maximum,excluded.maximum)",time/60*60,r.Id,v);
        }tx.Commit();}
    }
    public void PutUsage(Usage u)
    {
        if(u.Time<J.Now-RetentionDays*86400L||u.Time>J.Now+300)return;
        lock(gate) Execute("""
        INSERT INTO usage VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12)
        ON CONFLICT(id) DO UPDATE SET input=max(input,excluded.input),output=max(output,excluded.output),cached=max(cached,excluded.cached),cache_write=max(cache_write,excluded.cache_write),reasoning=max(reasoning,excluded.reasoning),duration=coalesce(excluded.duration,duration),rate=coalesce(excluded.rate,rate),status=excluded.status
        """,u.Id,u.Source,u.Model,u.Session,u.Time,Math.Max(0,u.Input),Math.Max(0,u.Output),Math.Max(0,u.Cached),Math.Max(0,u.CacheWrite),Math.Max(0,u.Reasoning),u.DurationMs,u.DecodeRate,u.Status);
    }
    public void Session(string id,string source,string model,string state,long time)
    {
        lock(gate) Execute("INSERT INTO sessions VALUES(@p0,@p1,@p2,@p3,@p4) ON CONFLICT(id) DO UPDATE SET model=CASE WHEN excluded.model='' THEN model ELSE excluded.model END,state=excluded.state,last_seen=excluded.last_seen WHERE excluded.last_seen>=last_seen",id,source,model,state,time);
    }
    public List<SessionInfo> Sessions(int days=1,string search="")
    {
        lock(gate){using var c=Command("SELECT s.id,s.source,s.model,s.state,s.last_seen,coalesce(sum(u.input),0),coalesce(sum(u.output),0),coalesce(sum(u.cached),0) FROM sessions s LEFT JOIN usage u ON u.session=s.id AND u.time>=@p0 WHERE s.last_seen>=@p0 AND (@p1='' OR instr(lower(s.id||' '||s.source||' '||s.model||' '||s.state),lower(@p1))>0) GROUP BY s.id ORDER BY s.last_seen DESC LIMIT 100",J.Now-days*86400L,search.Trim());
        using var r=c.ExecuteReader();var rows=new List<SessionInfo>();while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7)));return rows;}
    }
    public List<Usage> UsageSince(long since)
    {
        lock(gate){using var c=Command("SELECT * FROM usage WHERE time>=@p0 ORDER BY time DESC LIMIT 20000",since);using var r=c.ExecuteReader();var rows=new List<Usage>();
        while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.IsDBNull(10)?null:r.GetDouble(10),r.IsDBNull(11)?null:r.GetDouble(11),r.GetString(12)));return rows;}
    }
    public (long Input,long Output,long Cached,long Requests) Totals(long since)
    {
        lock(gate){using var c=Command("SELECT coalesce(sum(input),0),coalesce(sum(output),0),coalesce(sum(cached),0),count(*) FROM usage WHERE time>=@p0",since);using var r=c.ExecuteReader();r.Read();return(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3));}
    }
    public void ExportUsage(string file,long since)
    {
        // Stream the full retained range without the UI's row cap or an unbounded in-memory list.
        static string Q(string value){if(value.Length>0&&"=+-@\t\r".Contains(value[0]))value="'"+value;return "\""+value.Replace("\"","\"\"")+"\"";}
        lock(gate)
        {
            using var writer=new StreamWriter(file);
            writer.WriteLine("UTC,Source,Model,Session,InputTokens,OutputTokens,CachedInputTokens,CacheWriteTokens,ReasoningTokens,DurationMs,DecodeTokensPerSecond,Status");
            using var c=Command("SELECT * FROM usage WHERE time>=@p0 ORDER BY time DESC",since);using var r=c.ExecuteReader();
            while(r.Read())
            {
                var cells=new List<string>{Q(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).ToString("O")),Q(r.GetString(1)),Q(r.GetString(2)),Q(r.GetString(3))};
                for(int i=5;i<=11;i++)cells.Add(r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),System.Globalization.CultureInfo.InvariantCulture)??"");
                cells.Add(Q(r.GetString(12)));writer.WriteLine(string.Join(',',cells));
            }
        }
    }
    public List<MetricPoint> History(string id,long since)
    {
        lock(gate){long bucket=J.Now-since>7*86400?3600:J.Now-since>86400?300:60;
        using var c=Command("SELECT time/@p2*@p2,sum(total)/sum(count),min(minimum),max(maximum) FROM minute_metrics WHERE id=@p0 AND time>=@p1 GROUP BY time/@p2 ORDER BY time",id,since,bucket);
        using var r=c.ExecuteReader();var rows=new List<MetricPoint>();while(r.Read())rows.Add(new(r.GetInt64(0),r.GetDouble(1),r.GetDouble(2),r.GetDouble(3)));return rows;}
    }
    public (long Offset,string State) Checkpoint(string path)
    {
        lock(gate){using var c=Command("SELECT offset,state FROM checkpoints WHERE path=@p0",path);using var r=c.ExecuteReader();return r.Read()?(r.GetInt64(0),r.GetString(1)):(0,"{}");}
    }
    public void Checkpoint(string path,long offset,string state)
    {lock(gate)Execute("INSERT INTO checkpoints VALUES(@p0,@p1,@p2) ON CONFLICT(path) DO UPDATE SET offset=excluded.offset,state=excluded.state",path,offset,state);}
    public void ResetImportCheckpoints(){lock(gate)Execute("DELETE FROM checkpoints");}
    public void Alert(string key,string title,string detail,bool active)
    {lock(gate)Execute("INSERT INTO alerts VALUES(@p0,@p1,@p2,@p3,@p4) ON CONFLICT(key) DO UPDATE SET title=excluded.title,detail=excluded.detail,time=CASE WHEN active<>excluded.active THEN excluded.time ELSE time END,active=excluded.active",key,title,detail,J.Now,active?1:0);}
    public List<AlertInfo> Alerts()
    {
        lock(gate){using var c=Command("SELECT * FROM alerts ORDER BY active DESC,time DESC LIMIT 100");using var r=c.ExecuteReader();var a=new List<AlertInfo>();while(r.Read())a.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4)==1));return a;}
    }
    public void Retain(long now)
    {
        lock(gate){long cutoff=now-RetentionDays*86400L;Execute("DELETE FROM metrics WHERE time<@p0",now-7*86400);Execute("DELETE FROM minute_metrics WHERE time<@p0",cutoff);Execute("DELETE FROM usage WHERE time<@p0",cutoff);Execute("DELETE FROM sessions WHERE last_seen<@p0",cutoff);Execute("DELETE FROM alerts WHERE time<@p0 AND active=0",cutoff);Execute("PRAGMA wal_checkpoint(PASSIVE)");}
    }
    public string Backup(string folder)
    {
        Directory.CreateDirectory(folder);
        string file=Path.Combine(folder,"pulse-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".db"), temp=file+".partial";
        lock(gate){using var dest=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=temp,Pooling=false}.ToString());dest.Open();db.BackupDatabase(dest);using var c=dest.CreateCommand();c.CommandText="PRAGMA integrity_check";if((string?)c.ExecuteScalar()!="ok")throw new InvalidDataException("Backup verification failed");}
        File.Move(temp,file,true);
        using(var f=File.OpenRead(file)) File.WriteAllText(file+".sha256",Convert.ToHexString(SHA256.HashData(f)));
        // Snapshot coverage follows the selected history window (13 weeks by default).
        var files=Directory.GetFiles(folder,"pulse-*.db").OrderDescending().ToArray();
        var keep=files.GroupBy(f=>File.GetLastWriteTimeUtc(f).Date).Take(7).Select(g=>g.First()).ToHashSet();
        foreach(var f in files.GroupBy(f=>{var d=File.GetLastWriteTimeUtc(f).Date;return d.AddDays(-((int)d.DayOfWeek+6)%7);}).Take((int)Math.Ceiling(RetentionDays/7.0)).Select(g=>g.First()))keep.Add(f);
        foreach(var f in files.Where(f=>!keep.Contains(f))){File.Delete(f);File.Delete(f+".sha256");}
        return file;
    }
    public static void VerifyBackup(string file)
    {
        if(!File.Exists(file+".sha256"))throw new InvalidDataException("Checksum missing");
        using(var f=File.OpenRead(file))if(!string.Equals(Convert.ToHexString(SHA256.HashData(f)),File.ReadAllText(file+".sha256").Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Checksum mismatch");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();using var c=db.CreateCommand();c.CommandText="PRAGMA integrity_check";if((string?)c.ExecuteScalar()!="ok")throw new InvalidDataException("Database integrity failed");c.CommandText="PRAGMA user_version";if(Convert.ToInt32(c.ExecuteScalar())!=1)throw new InvalidDataException("Unsupported schema");
    }
    public void Dispose(){lock(gate)db.Dispose();}
}
