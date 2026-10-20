using System.Text.Json;

namespace AiPulse;

public record SamplePayload(long Time,List<Reading> Readings);
public record SessionPayload(string Id,string Source,string Model,string State,long Time);
public record RollupPayload(long Time,Reading Sensor,double Total,long Count,double Minimum,double Maximum);
public record SyncEvent(long Sequence,string Kind,string Payload);

public sealed partial class Storage
{
    public bool Replication {get;}
    public long QueueBudgetBytes {get;set;}=2L*1024*1024*1024;
    void InitializeReplication()=>Execute("""
        CREATE TABLE IF NOT EXISTS sync_events(seq INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,payload TEXT NOT NULL,bytes INTEGER NOT NULL,created INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS sync_state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sync_queue_stats(id INTEGER PRIMARY KEY CHECK(id=1),bytes INTEGER NOT NULL);
        INSERT OR IGNORE INTO sync_queue_stats SELECT 1,coalesce(sum(bytes),0) FROM sync_events;
        CREATE TRIGGER IF NOT EXISTS sync_event_insert AFTER INSERT ON sync_events BEGIN UPDATE sync_queue_stats SET bytes=bytes+new.bytes WHERE id=1; END;
        CREATE TRIGGER IF NOT EXISTS sync_event_delete AFTER DELETE ON sync_events BEGIN UPDATE sync_queue_stats SET bytes=bytes-old.bytes WHERE id=1; END;
        """);
    public string SyncState(string key){lock(gate)return Convert.ToString(Scalar("SELECT value FROM sync_state WHERE key=@p0",key))??"";}
    public void SyncState(string key,string value){lock(gate)Execute("INSERT INTO sync_state VALUES(@p0,@p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value",key,value);}
    void AppendEvent<T>(string kind,T payload)
    {
        if(!Replication)return;
        var json=JsonSerializer.Serialize(payload);int bytes=System.Text.Encoding.UTF8.GetByteCount(json);
        long size=Convert.ToInt64(Scalar("SELECT bytes FROM sync_queue_stats WHERE id=1"));
        if(size+bytes>QueueBudgetBytes)throw new IOException("Durable sync queue is full. Collection is paused until delivery resumes.");
        Execute("INSERT INTO sync_events(kind,payload,bytes,created) VALUES(@p0,@p1,@p2,@p3)",kind,json,bytes,J.Now);
    }
    public (long Count,long Bytes,long Oldest) QueueInfo()
    {
        lock(gate){using var c=Command("SELECT count(*),(SELECT bytes FROM sync_queue_stats WHERE id=1),coalesce(min(created),0) FROM sync_events");using var r=c.ExecuteReader();r.Read();return(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2));}
    }
    public List<SyncEvent> Events(long after=0,int limit=100,int byteLimit=750_000)
    {
        lock(gate){using var c=Command("SELECT seq,kind,payload,bytes FROM sync_events WHERE seq>@p0 ORDER BY seq LIMIT @p1",after,Math.Clamp(limit,1,1000));using var r=c.ExecuteReader();var rows=new List<SyncEvent>();long size=0;
            while(r.Read()){long bytes=r.GetInt64(3);if(rows.Count>0&&size+bytes>byteLimit)break;size+=bytes;rows.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2)));}return rows;}
    }
    public void Acknowledge(IEnumerable<long> sequences)
    {
        lock(gate){using var tx=db.BeginTransaction();foreach(var seq in sequences)Execute("DELETE FROM sync_events WHERE seq=@p0",seq);tx.Commit();}
    }
    // A separate database for every host makes every legacy history/token/export query host-scoped.
    // Commit the data, replay cursor and downstream journal together, before returning an ACK.
    public void ApplyBatch(string stream,List<SyncEvent> events,bool forward)
    {
        foreach(var e in events)ValidateEvent(e);
        lock(gate)
        {
            using var tx=db.BeginTransaction();long.TryParse(SyncState("applied:"+stream),out long applied);
            foreach(var e in events.OrderBy(e=>e.Sequence))
            {
                if(e.Sequence<=applied)continue;
                switch(e.Kind)
                {
                    case "sample":var s=JsonSerializer.Deserialize<SamplePayload>(e.Payload)!;SampleCore(s.Time,s.Readings);break;
                    case "usage":UsageCore(JsonSerializer.Deserialize<Usage>(e.Payload)!);break;
                    case "session":SessionCore(JsonSerializer.Deserialize<SessionPayload>(e.Payload)!);break;
                    case "alert":AlertCore(JsonSerializer.Deserialize<AlertInfo>(e.Payload)!);break;
                    case "rollup":
                        var p=JsonSerializer.Deserialize<RollupPayload>(e.Payload)!;
                        Execute("INSERT INTO sensors VALUES(@p0,@p1,@p2,@p3,@p4) ON CONFLICT(id) DO UPDATE SET name=excluded.name,device=excluded.device,unit=excluded.unit,source=excluded.source",p.Sensor.Id,p.Sensor.Name,p.Sensor.Device,p.Sensor.Unit,p.Sensor.Source);
                        Execute("INSERT INTO minute_metrics VALUES(@p0,@p1,@p2,@p3,@p4,@p5) ON CONFLICT(time,id) DO UPDATE SET total=excluded.total,count=excluded.count,minimum=excluded.minimum,maximum=excluded.maximum",p.Time,p.Sensor.Id,p.Total,p.Count,p.Minimum,p.Maximum);break;
                }
                if(forward)Execute("INSERT INTO sync_events(kind,payload,bytes,created) VALUES(@p0,@p1,@p2,@p3)",e.Kind,e.Payload,System.Text.Encoding.UTF8.GetByteCount(e.Payload),J.Now);
                applied=e.Sequence;
            }
            SyncState("applied:"+stream,applied.ToString());tx.Commit();
        }
    }
    public static void ValidateEvent(SyncEvent e)
    {
        if(e.Sequence<=0||e.Payload.Length>1_000_000)throw new InvalidDataException("Invalid event bounds");
        static void Text(string? s,int max=512){if(s==null||s.Length>max)throw new InvalidDataException("Invalid telemetry text");}
        static void Time(long t){if(t<J.Now-366L*86400||t>J.Now+300)throw new InvalidDataException("Observation outside supported clock/retention window");}
        static void Sensor(Reading r){Text(r.Id);Text(r.Name);Text(r.Device);Text(r.Unit,32);Text(r.Source,128);if(r.Value.HasValue&&!double.IsFinite(r.Value.Value))throw new InvalidDataException("Non-finite metric");}
        switch(e.Kind)
        {
            case "sample":var s=JsonSerializer.Deserialize<SamplePayload>(e.Payload)??throw new InvalidDataException();Time(s.Time);if(s.Readings.Count>5000)throw new InvalidDataException();foreach(var r in s.Readings)Sensor(r);break;
            case "usage":var u=JsonSerializer.Deserialize<Usage>(e.Payload)??throw new InvalidDataException();Time(u.Time);Text(u.Id);Text(u.Source);Text(u.Model);Text(u.Session);Text(u.Status);if(new[]{u.Input,u.Output,u.Cached,u.CacheWrite,u.Reasoning}.Any(v=>v<0||v>1_000_000_000_000))throw new InvalidDataException();break;
            case "session":var p=JsonSerializer.Deserialize<SessionPayload>(e.Payload)??throw new InvalidDataException();Time(p.Time);Text(p.Id);Text(p.Source);Text(p.Model);Text(p.State);break;
            case "alert":var a=JsonSerializer.Deserialize<AlertInfo>(e.Payload)??throw new InvalidDataException();Time(a.Time);Text(a.Key);Text(a.Title);Text(a.Detail,2048);break;
            case "rollup":var v=JsonSerializer.Deserialize<RollupPayload>(e.Payload)??throw new InvalidDataException();Time(v.Time);Sensor(v.Sensor);if(v.Count<=0||!double.IsFinite(v.Total)||!double.IsFinite(v.Minimum)||!double.IsFinite(v.Maximum))throw new InvalidDataException();break;
            default:throw new InvalidDataException("Unsupported telemetry kind");
        }
    }
    public void RetainJournal(long now){lock(gate)Execute("DELETE FROM sync_events WHERE created<@p0",now-RetentionDays*86400L);}
}
