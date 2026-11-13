namespace AiPulse;

public record AgentSpawn(string Id,string Source,long Time);
public record AgentSpawnCount(string Source,long Count);

public sealed partial class Storage
{
    void InitializeAgentSpawns()=>Execute("""
        CREATE TABLE IF NOT EXISTS agent_spawns(id TEXT PRIMARY KEY,source TEXT NOT NULL,time INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS agent_spawns_time ON agent_spawns(time,id);
        """);

    public void RecordAgentSpawn(string source,string identity,long time)
    {
        long now=J.Now;
        if(source.Length is 0 or >128||identity.Length is 0 or >1024||source.Any(char.IsControl)||
           time<=0||time<now-RetentionDays*86400L||time>now+300)return;
        var spawn=new AgentSpawn(J.Hash(source+"\n"+identity),source,time);
        lock(gate)
        {
            using var tx=db.BeginTransaction();
            if(AgentSpawnCore(spawn))AppendEvent("agent-spawn",spawn);
            tx.Commit();
        }
    }

    bool AgentSpawnCore(AgentSpawn spawn)
    {
        using var command=Command("""
            INSERT INTO agent_spawns VALUES(@p0,@p1,@p2)
            ON CONFLICT(id) DO UPDATE SET time=excluded.time WHERE excluded.time<agent_spawns.time
            """,spawn.Id,spawn.Source,spawn.Time);
        return command.ExecuteNonQuery()>0;
    }

    public List<AgentSpawnCount> AgentSpawns(long since,long until)
    {
        lock(gate)
        {
            using var command=Command("SELECT source,count(*) FROM agent_spawns WHERE time BETWEEN @p0 AND @p1 GROUP BY source ORDER BY source",since,until);
            using var reader=command.ExecuteReader();
            var results=new List<AgentSpawnCount>();
            while(reader.Read())results.Add(new(reader.GetString(0),reader.GetInt64(1)));
            return results;
        }
    }
}
