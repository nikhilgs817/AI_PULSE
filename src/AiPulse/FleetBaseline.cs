using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AiPulse;

public record BaselineCursor(int Stage=0,long Time=0,string Key="",long Sequence=0);
public record BaselineTicket(string Id,long JournalSequence,int RetentionDays);
public record BaselinePage(List<SyncEvent> Events,BaselineCursor Next,bool Complete);

public sealed partial class Storage
{
    public (long First,long Latest) JournalRange()
    {
        lock(gate){long latest=Convert.ToInt64(Scalar("SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name='sync_events'),0)"));long first=Convert.ToInt64(Scalar("SELECT coalesce(min(seq),@p0) FROM sync_events",latest+1));return(first,latest);}
    }
    public long ReplicaSnapshot(string file)
    {
        lock(gate)
        {
            long sequence=JournalRange().Latest;Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());destination.Open();db.BackupDatabase(destination);return sequence;
        }
    }
    public void ResetReplica()
    {
        lock(gate){using var tx=db.BeginTransaction();Execute("DELETE FROM metrics;DELETE FROM minute_metrics;DELETE FROM sensors;DELETE FROM usage;DELETE FROM sessions;DELETE FROM alerts;DELETE FROM checkpoints;DELETE FROM sync_state WHERE key<>'last-snapshot';");tx.Commit();}
    }
    public BaselinePage Baseline(BaselineCursor cursor)
    {
        if(cursor.Stage is <0 or >4||cursor.Time<0||cursor.Key.Length>512||cursor.Sequence<0)throw new InvalidDataException("Invalid baseline cursor");
        lock(gate)
        {
            var rows=new List<SyncEvent>();int bytes=0;var next=cursor;
            while(next.Stage<4&&rows.Count<1000)
            {
                string sql=next.Stage switch
                {
                    0=>"SELECT * FROM usage WHERE (time,id)>(@p0,@p1) ORDER BY time,id LIMIT @p2",
                    1=>"SELECT m.time,s.id,s.name,s.device,s.unit,s.source,m.total,m.count,m.minimum,m.maximum FROM minute_metrics m JOIN sensors s ON s.id=m.id WHERE (m.time,m.id)>(@p0,@p1) ORDER BY m.time,m.id LIMIT @p2",
                    2=>"SELECT id,source,model,state,last_seen FROM sessions WHERE id>@p1 ORDER BY id LIMIT @p2",
                    _=>"SELECT key,title,detail,time,active FROM alerts WHERE key>@p1 ORDER BY key LIMIT @p2"
                };
                using var command=Command(sql,next.Time,next.Key,1000-rows.Count);using var reader=command.ExecuteReader();bool any=false;
                while(reader.Read())
                {
                    any=true;string kind,payload,key;long time=0;
                    switch(next.Stage)
                    {
                        case 0:
                            var u=new Usage(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6),reader.GetInt64(7),reader.GetInt64(8),reader.GetInt64(9),reader.IsDBNull(10)?null:reader.GetDouble(10),reader.IsDBNull(11)?null:reader.GetDouble(11),reader.GetString(12));
                            kind="usage";payload=JsonSerializer.Serialize(u);time=u.Time;key=u.Id;break;
                        case 1:
                            var p=new RollupPayload(reader.GetInt64(0),new(reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),null,reader.GetString(5)),reader.GetDouble(6),reader.GetInt64(7),reader.GetDouble(8),reader.GetDouble(9));
                            kind="rollup";payload=JsonSerializer.Serialize(p);time=p.Time;key=p.Sensor.Id;break;
                        case 2:
                            var s=new SessionPayload(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4));kind="session";payload=JsonSerializer.Serialize(s);key=s.Id;break;
                        default:
                            var a=new AlertInfo(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),reader.GetInt64(4)==1);kind="alert";payload=JsonSerializer.Serialize(a);key=a.Key;break;
                    }
                    int size=System.Text.Encoding.UTF8.GetByteCount(payload);if(rows.Count>0&&bytes+size>750_000)return new(rows,next,false);
                    bytes+=size;next=new(next.Stage,time,key,next.Sequence+1);rows.Add(new(next.Sequence,kind,payload));
                }
                if(!any)next=new(next.Stage+1,0,"",next.Sequence);
            }
            return new(rows,next,next.Stage==4);
        }
    }
}
