using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AiPulse;

public static class UsageDiagnostics
{
    public static int Run()
    {
        var report=new Dictionary<string,object?>{["At"]=J.Now,["Database"]=Paths.Database,["DataRoot"]=Paths.Root,["Application"]=AppContext.BaseDirectory};
        bool failed=false;
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Paths.Database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        try
        {
            db.Open();
            void Query(string key,string sql)
            {
                try{using var c=db.CreateCommand();c.CommandText=sql;using var r=c.ExecuteReader();var rows=new List<Dictionary<string,object?>>();while(r.Read()){var row=new Dictionary<string,object?>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(row);}report[key]=rows;}
                catch(SqliteException e){failed=true;report[key]=new{Error="SQLite query failed",e.SqliteErrorCode,e.SqliteExtendedErrorCode};}
            }
            Query("Engine","SELECT sqlite_version() AS version");Query("Files","PRAGMA database_list");Query("Integrity","PRAGMA quick_check(1)");
            if(report["Integrity"] is not List<Dictionary<string,object?>> integrity||integrity.Count!=1||
               !Equals(integrity[0].GetValueOrDefault("quick_check"),"ok"))failed=true;
            Query("StoreIdentity","SELECT value AS store_id FROM accounting_meta WHERE key='store_id'");
            Query("LastHardwareSample","SELECT max(time) AS time FROM metrics");
            Query("Sources","SELECT source,count(*) AS records,sum(input+output) AS tokens,max(time) AS last_record FROM usage GROUP BY source ORDER BY source");
            long now=J.Now,today=J.DayStart(DateTimeOffset.FromUnixTimeSeconds(now),TimeZoneInfo.Local);
            Query("Today",$"SELECT source,count(*) AS records,sum(input+output) AS tokens FROM usage WHERE time BETWEEN {today} AND {now} GROUP BY source ORDER BY source");
            Query("CollectionHealth","SELECT * FROM usage_sources");
        }
        catch(SqliteException e){failed=true;report["OpenError"]=new{e.SqliteErrorCode,e.SqliteExtendedErrorCode};}
        var settings=Paths.Load();report["Configuration"]=new{settings.ReadAgentUsage,settings.UsageRoots,settings.EnableGateway,settings.GatewayPort,settings.OllamaUrl,settings.TelemetryPort,settings.FleetDashboard,RelayConfigured=File.Exists(UsageRelay.ConfigFile)};
        // No secrets, request payloads or session bodies are included, even when the store fails.
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(Paths.Root,"usage-diagnostics.json"),json);Console.WriteLine(json);return failed?1:0;
    }
}
