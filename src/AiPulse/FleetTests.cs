using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiPulse;

public static class FleetTests
{
    public static async Task<int> Run()
    {
        var lines=new List<string>();string root=Path.Combine(Paths.Root,"fleet-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);lines.Add("PASS: "+name);}
        try
        {
            string hubRoot=Path.Combine(root,"hub");var connection=FleetFiles.InitializeHub(hubRoot);
            using var socket=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);socket.Start();int port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
            var cfg=FleetFiles.Read<HubConfig>(Path.Combine(hubRoot,"hub.json"));cfg.Port=port;cfg.ListenAddress="127.0.0.1";FleetFiles.Write(Path.Combine(hubRoot,"hub.json"),cfg);connection.Url="https://localhost:"+port;
            using var hub=new FleetHub(hubRoot);using var stop=new CancellationTokenSource();var running=hub.Run(stop.Token);
            try
            {
                using var admin=FleetFiles.Client(connection);bool ready=false;
                for(int i=0;i<50;i++){try{using var r=await admin.GetAsync("/fleet/v1/hosts");if(r.IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}await Task.Delay(100);}
                Check(ready,"Pinned HTTPS hub starts independently of the desktop");
                var anonymous=new FleetConnection{Url=connection.Url,CertificateSha256=connection.CertificateSha256};using var guest=FleetFiles.Client(anonymous);
                using(var denied=await guest.GetAsync("/fleet/v1/hosts"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Unauthenticated clients cannot query hosts or history");
                var bad=new FleetConnection{Url=connection.Url,CertificateSha256=new string('0',64)};using var wrongPin=FleetFiles.Client(bad);bool pinRejected=false;try{await wrongPin.GetAsync("/fleet/v1/hosts");}catch(HttpRequestException){pinRejected=true;}Check(pinRejected,"A mismatched hub certificate is rejected");
                var a=hub.Enroll(new(hub.Pair().Code,Guid.NewGuid().ToString("N"),"same-name","Private LAN"));
                string installB=Guid.NewGuid().ToString("N");var ticket=hub.Pair();var b=hub.Enroll(new(ticket.Code,installB,"same-name","VPN"));
                var retryEnrollment=hub.Enroll(new(ticket.Code,installB,"same-name","VPN"));Check(retryEnrollment==b,"A lost enrollment response can be retried by the same installation without creating a second host");
                bool codeRejected=false;try{hub.Enroll(new(ticket.Code,Guid.NewGuid().ToString("N"),"intruder","Private LAN"));}catch(UnauthorizedAccessException){codeRejected=true;}Check(codeRejected,"Pairing codes are single-use and bind a unique installation");
                using var sourceA=new Storage(Path.Combine(root,"source-a.db"),replication:true);using var sourceB=new Storage(Path.Combine(root,"source-b.db"),replication:true);
                long now=J.Now/60*60+10;sourceA.Sample(now,[new("cpu.load","CPU","same-device","%",25,"fixture")]);sourceB.Sample(now,[new("cpu.load","CPU","same-device","%",75,"fixture")]);
                sourceA.PutUsage(new("same-request","Ollama","same-model","same-session",now,10,3,0,0,0,null,null,"Completed"));sourceB.PutUsage(new("same-request","Ollama","same-model","same-session",now,50,7,0,0,0,null,null,"Completed"));
                sourceA.Session("same-session","Ollama","same-model","Working",now);sourceB.Session("same-session","Ollama","same-model","Working",now);
                var ah=hub.AuthenticateHost(a.Token)!;var bh=hub.AuthenticateHost(b.Token)!;
                // Simulate a lost ACK: resend exactly the same committed upload.
                var batchA=new IngestBatch(ah.InstallationId,sourceA.Events());var ack=hub.Ingest(ah,batchA);hub.Ingest(ah,batchA);hub.Ingest(bh,new(installB,sourceB.Events()));
                using var mirrorA=new Storage(Path.Combine(root,"mirror-a.db"));using var mirrorB=new Storage(Path.Combine(root,"mirror-b.db"));
                mirrorA.ApplyBatch("hub",hub.View(a.HostId,0)!.Events,false);mirrorB.ApplyBatch("hub",hub.View(b.HostId,0)!.Events,false);
                Check(mirrorA.Totals(now-1).Input==10&&mirrorB.Totals(now-1).Input==50&&mirrorA.History("cpu.load",now/60*60).Single().Average==25&&mirrorB.History("cpu.load",now/60*60).Single().Average==75,"Identical host/device/model/session/request names remain isolated");
                Check(mirrorA.Totals(now-1).Requests==1&&mirrorA.History("cpu.load",now/60*60).Single().Maximum==25,"Lost acknowledgements and upload replay do not double-count tokens or metrics");
                sourceA.Acknowledge(ack.Accepted);Check(sourceA.QueueInfo().Count==0,"Only committed, explicitly acknowledged events leave the durable queue");
                sourceA.Sample(now+1,[new("cpu.load","CPU","same-device","%",35,"fixture")]);sourceA.PutUsage(new("offline-request","Ollama","same-model","same-session",now+1,20,4,0,0,0,null,null,"Completed"));
                var queued=sourceA.Events();Check(queued.Count==2&&sourceA.QueueInfo().Bytes>0,"Disconnected collection keeps both hardware and token observations on disk");
                var restartPath=Path.Combine(root,"restart.db");using(var before=new Storage(restartPath,replication:true))before.PutUsage(new("survives","Ollama","m","s",now,7,1,0,0,0,null,null,"Completed"));
                using(var after=new Storage(restartPath,replication:true))Check(after.Events().Count==1&&after.Totals(now-1).Input==7,"Agent restart preserves unacknowledged data and identity-independent record IDs");
                hub.Ingest(ah,new(ah.InstallationId,queued));mirrorA.ApplyBatch("hub",hub.View(a.HostId,0)!.Events,false);
                Check(mirrorA.Totals(now-1).Input==30&&mirrorA.History("cpu.load",now/60*60,now+1).Single().Maximum==35,"Reconnect backfills original timestamps and exact token counts");
                sourceA.QueueBudgetBytes=0;bool full=false;try{sourceA.Sample(now+2,[new("not-committed","CPU","d","%",55,"fixture")]);}catch(IOException){full=true;}
                Check(full&&sourceA.History("not-committed",now-60,now+3).Count==0,"Queue exhaustion rolls back the local observation instead of silently losing replication");
                Check(FleetHub.ConnectionState(now,now,now)=="Live"&&FleetHub.ConnectionState(now,now-20,now)=="Stale"&&FleetHub.ConnectionState(now-40,now,now)=="Offline","Collector freshness and host connection status are distinct");
                var clientConnection=new FleetConnection{Url=connection.Url,CertificateSha256=connection.CertificateSha256,Token=a.Token};using var agent=FleetFiles.Client(clientConnection);
                using(var denied=await agent.GetAsync("/fleet/v1/hosts"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"An ingestion credential cannot read hosts or administer the hub");
                using(var live=await agent.PostAsJsonAsync("/fleet/v1/live",new HostSnapshot{Name="same-name",SampledAt=J.Now,Readings=[new("cpu.load","CPU","d","%",25,"fixture")],QueueCount=200}))Check(live.IsSuccessStatusCode&&hub.Hosts().Single(h=>h.Id==a.HostId).Status=="Live","Live snapshots stay current independently of a history backlog");
                var malformed=new IngestBatch(ah.InstallationId,[new(999,"execute",JsonSerializer.Serialize(new{command="must-not-run"}))]);using(var rejected=await agent.PostAsJsonAsync("/fleet/v1/ingest",malformed))Check(rejected.StatusCode==HttpStatusCode.BadRequest,"The protocol rejects arbitrary operations and unsupported telemetry");
                hub.Revoke(a.HostId);using(var revoked=await agent.PostAsJsonAsync("/fleet/v1/live",new HostSnapshot()))Check(revoked.StatusCode==HttpStatusCode.Unauthorized,"Revocation immediately rejects that host without affecting other hosts");
                Check(hub.AuthenticateHost(b.Token)!=null,"Revoking one host preserves another host's access");
                hub.Live(bh,new HostSnapshot{Name="same-name",SampledAt=J.Now,Readings=[new("cpu.load","CPU","d","%",75,"fixture")]});
                using(var dashboard=new FleetDashboard(connection){SelectedHost=b.HostId})
                {
                    dashboard.Start();for(int i=0;i<100&&(dashboard.SelectedMonitor?.Store.Totals(now-1).Input!=50||dashboard.SelectedMonitor?.Value("cpu.load")!=75);i++)await Task.Delay(100);
                    Check(dashboard.SelectedMonitor?.Store.Totals(now-1).Input==50&&dashboard.SelectedMonitor.Value("cpu.load")==75,"The dashboard queries and caches only the selected host's readings and token history");
                }
                bool plainRejected=false;try{FleetFiles.ValidateConnection(new(){Url="http://host:11440",CertificateSha256=connection.CertificateSha256});}catch(InvalidDataException){plainRejected=true;}Check(plainRejected,"All network profiles reject plaintext transport");
                var internet=new FleetConnection{Url=connection.Url,NetworkType="Internet",Token=connection.Token};using var publicClient=FleetFiles.Client(internet);bool untrusted=false;try{await publicClient.GetAsync("/fleet/v1/hosts");}catch(HttpRequestException){untrusted=true;}Check(untrusted,"Internet mode requires a publicly trusted certificate");
                if(OperatingSystem.IsWindows())
                {
                    bool entered=false,blocked=false;try{new MetadataAccess("S-1-5-21-1-2-3-9999").Read(()=>{entered=true;return 1;});}catch(MetadataUserUnavailableException){blocked=true;}
                    Check(blocked&&!entered,"Windows metadata reads never execute under the service identity when the configured user is unavailable or mismatched");
                }
                string legacyFile=Path.Combine(root,"legacy.db");
                using(var legacy=new Storage(legacyFile)){legacy.Sample(now,[new("legacy.cpu","CPU","d","%",42,"fixture")]);legacy.PutUsage(new("legacy-token","Ollama","legacy","s",now,123,4,0,0,0,null,null,"Completed"));}
                using(var imported=new Storage(legacyFile,replication:true))
                using(var target=new Storage(Path.Combine(root,"migrated.db")))
                {
                    imported.SeedExistingHistory();var count=imported.QueueInfo().Count;imported.SeedExistingHistory();target.ApplyBatch("migration",imported.Events(),false);
                    Check(imported.QueueInfo().Count==count&&target.Totals(now-1).Input==123&&target.History("legacy.cpu",now/60*60).Single().Average==42,"Standalone migration seeds retained minute history and exact usage once without multiplying records");
                }
                var baselineHost=hub.Enroll(new(hub.Pair().Code,Guid.NewGuid().ToString("N"),"baseline-host","Private LAN"));var baselineIdentity=hub.AuthenticateHost(baselineHost.Token)!;
                hub.Ingest(baselineIdentity,new(baselineIdentity.InstallationId,[new(1,"sample",JsonSerializer.Serialize(new SamplePayload(now,[new("baseline.cpu","CPU","d","%",40,"fixture")]))),new(2,"usage",JsonSerializer.Serialize(new Usage("base-one","Ollama","m","s",now,15,1,0,0,0,null,null,"Completed")))]));
                var baselineTicket=hub.BeginBaseline(baselineHost.HostId);
                hub.Ingest(baselineIdentity,new(baselineIdentity.InstallationId,[new(3,"usage",JsonSerializer.Serialize(new Usage("base-two","Ollama","m","s",now,5,1,0,0,0,null,null,"Completed"))),new(4,"sample",JsonSerializer.Serialize(new SamplePayload(now+1,[new("baseline.cpu","CPU","d","%",50,"fixture")])))]));
                var paged=Enumerable.Range(0,2222).Select(i=>new SyncEvent(i+5,"usage",JsonSerializer.Serialize(new Usage("paged-"+i.ToString("D4"),"Ollama","m","s",now,0,0,0,0,0,null,null,"Completed")))).ToList();
                foreach(var chunk in paged.Chunk(1000))hub.Ingest(baselineIdentity,new(baselineIdentity.InstallationId,chunk.ToList()));
                using(var baselineTarget=new Storage(Path.Combine(root,"baseline-mirror.db")))
                {
                    BaselineCursor cursor=new();bool complete=false;while(!complete){var page=hub.ReadBaseline(baselineHost.HostId,baselineTicket.Id,cursor);baselineTarget.ApplyBatch("baseline",page.Events,false);cursor=page.Next;complete=page.Complete;}
                    Check(baselineTarget.Totals(now-1).Input==15,"History snapshot stays consistent while newer observations arrive at the hub");
                    baselineTarget.ApplyBatch("live-after-snapshot",hub.View(baselineHost.HostId,baselineTicket.JournalSequence)!.Events,false);
                    Check(baselineTarget.Totals(now-1).Input==20&&baselineTarget.History("baseline.cpu",now/60*60,now+1).Single().Average==45,"Replay after a snapshot preserves exact token totals and sensor means without overlapping counts");
                }
                hub.ReleaseBaseline(baselineHost.HostId,baselineTicket.Id);
                using(var warmDashboard=new FleetDashboard(connection){SelectedHost=baselineHost.HostId})
                {
                    warmDashboard.Start();for(int i=0;i<150&&(warmDashboard.SelectedMonitor?.Store.Totals(now-1).Requests!=2224||warmDashboard.SelectedMonitor?.Store.SyncState("baseline-complete")!="yes");i++)await Task.Delay(100);
                    Check(warmDashboard.SelectedMonitor?.Store.Totals(now-1).Requests==2224,"Paged snapshots resume by stable keys across more than 2,000 identical-timestamp records");
                }
                hub.Ingest(baselineIdentity,new(baselineIdentity.InstallationId,[new(2227,"usage",JsonSerializer.Serialize(new Usage("missed-offline","Ollama","m","s",now,7,0,0,0,0,null,null,"Completed")))]));
                using(var expireJournal=new Storage(Path.Combine(hubRoot,"hosts",baselineHost.HostId,"pulse.db")))expireJournal.RetainJournal(J.Now+5*3600);
                var boundedView=hub.View(baselineHost.HostId,0)!;Check(boundedView.Events.Count==0&&boundedView.FirstSequence==boundedView.LatestSequence+1,"The four-hour delivery journal expires while its stable high-water mark survives");
                hub.Ingest(baselineIdentity,new(baselineIdentity.InstallationId,[new(2228,"usage",JsonSerializer.Serialize(new Usage("new-current","Ollama","m","s",now,4,0,0,0,0,null,null,"Completed")))]));
                using(var resumed=new FleetDashboard(connection){SelectedHost=baselineHost.HostId})
                {
                    resumed.Start();for(int i=0;i<150&&(resumed.SelectedMonitor?.Store.Totals(now-1).Input!=31||resumed.SelectedMonitor?.Store.Totals(now-1).Requests!=2226);i++)await Task.Delay(100);
                    Check(resumed.SelectedMonitor?.Store.Totals(now-1).Input==31&&resumed.SelectedMonitor.Store.Totals(now-1).Requests==2226,"An existing dashboard detects an expired replay cursor and recovers missed records from retained history");
                }
                var coldConnection=new FleetConnection{Url=connection.Url.Replace("localhost","127.0.0.1"),CertificateSha256=connection.CertificateSha256,Token=connection.Token};
                using(var coldDashboard=new FleetDashboard(coldConnection){SelectedHost=baselineHost.HostId})
                {
                    coldDashboard.Start();for(int i=0;i<150&&(coldDashboard.SelectedMonitor?.Store.Totals(now-1).Requests!=2226||coldDashboard.SelectedMonitor?.Store.SyncState("baseline-complete")!="yes");i++)await Task.Delay(100);
                    Check(coldDashboard.SelectedMonitor?.Store.Totals(now-1).Input==31&&coldDashboard.SelectedMonitor.Store.History("baseline.cpu",now/60*60,now+1).Single().Average==45,"A new dashboard obtains retained history after the old delivery journal has expired");
                }
                string agentRoot=Path.Combine(root,"real-agent");Directory.CreateDirectory(agentRoot);string realCode=hub.Pair().Code;
                var realConfig=new HostAgentConfig{Name="Live collector test",PairingCode=realCode,Connection=new(){Url=connection.Url,CertificateSha256=connection.CertificateSha256}};
                string realFile=Path.Combine(agentRoot,"agent.json");FleetFiles.Write(realFile,realConfig);
                using(var monitor=new MonitorService(new(){EnableGateway=false,ReadAgentUsage=false},Path.Combine(agentRoot,"pulse.db"),replication:true))
                using(var agentStop=new CancellationTokenSource())
                {
                    var worker=new FleetAgent(monitor,realConfig,realFile);var workerTask=worker.Run(agentStop.Token);
                    try
                    {
                        for(int i=0;i<200&&(realConfig.HostId==""||hub.View(realConfig.HostId,0)?.Events.Any(e=>e.Kind=="sample")!=true);i++)await Task.Delay(100);
                        Check(realConfig.HostId!=""&&hub.View(realConfig.HostId,0)!.Events.Any(e=>e.Kind=="sample"),"A real background Host Agent enrolls and sends this OS's hardware samples over HTTPS");
                        stop.Cancel();try{await running;}catch(OperationCanceledException){}
                        await Task.Delay(4500);long offlineAt=monitor.LastSample;Check(monitor.Store.QueueInfo().Count>0,"The actual Host Agent keeps collecting after the hub stops");
                        using var restarted=new FleetHub(hubRoot);using var restartStop=new CancellationTokenSource();var restartedTask=restarted.Run(restartStop.Token);
                        try
                        {
                            for(int i=0;i<300&&restarted.View(realConfig.HostId,0)?.Events.Any(e=>e.Kind=="sample"&&JsonSerializer.Deserialize<SamplePayload>(e.Payload)!.Time>=offlineAt)!=true;i++)await Task.Delay(100);
                            Check(restarted.View(realConfig.HostId,0)!.Events.Any(e=>e.Kind=="sample"&&JsonSerializer.Deserialize<SamplePayload>(e.Payload)!.Time>=offlineAt),"The actual Host Agent reconnects after hub restart and delivers its offline backlog");
                        }
                        finally{restartStop.Cancel();try{await restartedTask;}catch(OperationCanceledException){}}
                    }
                    finally{agentStop.Cancel();await workerTask;}
                }
                using(var cached=new FleetDashboard(connection){SelectedHost=b.HostId})
                {
                    cached.Start();Check(cached.SelectedMonitor?.Store.Totals(now-1).Input==50&&cached.SelectedMonitor.HostName=="same-name"&&!cached.SelectedMonitor.Fresh,"Restarting the dashboard while the hub is offline restores host-scoped cached history without presenting stale readings as live");
                }

            }
            finally{stop.Cancel();try{await running;}catch(OperationCanceledException){}}
            File.WriteAllLines(Path.Combine(Paths.Root,"fleet-test-results.txt"),lines);return 0;
        }
        catch(Exception e){lines.Add("FAIL: "+e);File.WriteAllLines(Path.Combine(Paths.Root,"fleet-test-results.txt"),lines);return 1;}
    }
}
