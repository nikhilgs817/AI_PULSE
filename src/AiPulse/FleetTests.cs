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
                long now=J.Now;sourceA.Sample(now,[new("cpu.load","CPU","same-device","%",25,"fixture")]);sourceB.Sample(now,[new("cpu.load","CPU","same-device","%",75,"fixture")]);
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
                using(var live=await agent.PostAsJsonAsync("/fleet/v1/live",new HostSnapshot{Name="same-name",SampledAt=now,Readings=[new("cpu.load","CPU","d","%",25,"fixture")],QueueCount=200}))Check(live.IsSuccessStatusCode&&hub.Hosts().Single(h=>h.Id==a.HostId).Status=="Live","Live snapshots stay current independently of a history backlog");
                var malformed=new IngestBatch(ah.InstallationId,[new(999,"execute",JsonSerializer.Serialize(new{command="must-not-run"}))]);using(var rejected=await agent.PostAsJsonAsync("/fleet/v1/ingest",malformed))Check(rejected.StatusCode==HttpStatusCode.BadRequest,"The protocol rejects arbitrary operations and unsupported telemetry");
                hub.Revoke(a.HostId);using(var revoked=await agent.PostAsJsonAsync("/fleet/v1/live",new HostSnapshot()))Check(revoked.StatusCode==HttpStatusCode.Unauthorized,"Revocation immediately rejects that host without affecting other hosts");
                Check(hub.AuthenticateHost(b.Token)!=null,"Revoking one host preserves another host's access");
                hub.Live(bh,new HostSnapshot{Name="same-name",SampledAt=J.Now,Readings=[new("cpu.load","CPU","d","%",75,"fixture")]});
                using(var dashboard=new FleetDashboard(connection){SelectedHost=b.HostId})
                {
                    dashboard.Start();for(int i=0;i<100&&dashboard.SelectedMonitor?.Store.Totals(now-1).Input!=50;i++)await Task.Delay(100);
                    Check(dashboard.SelectedMonitor?.Store.Totals(now-1).Input==50&&dashboard.SelectedMonitor.Value("cpu.load")==75,"The dashboard queries and caches only the selected host's readings and token history");
                }
                bool plainRejected=false;try{FleetFiles.ValidateConnection(new(){Url="http://host:11440",CertificateSha256=connection.CertificateSha256});}catch(InvalidDataException){plainRejected=true;}Check(plainRejected,"All network profiles reject plaintext transport");
                var internet=new FleetConnection{Url=connection.Url,NetworkType="Internet",Token=connection.Token};using var publicClient=FleetFiles.Client(internet);bool untrusted=false;try{await publicClient.GetAsync("/fleet/v1/hosts");}catch(HttpRequestException){untrusted=true;}Check(untrusted,"Internet mode requires a publicly trusted certificate");
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

            }
            finally{stop.Cancel();try{await running;}catch(OperationCanceledException){}}
            File.WriteAllLines(Path.Combine(Paths.Root,"fleet-test-results.txt"),lines);return 0;
        }
        catch(Exception e){lines.Add("FAIL: "+e);File.WriteAllLines(Path.Combine(Paths.Root,"fleet-test-results.txt"),lines);return 1;}
    }
}
