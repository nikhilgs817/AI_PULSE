using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiPulse;

public sealed class FleetAgent(MonitorService monitor,HostAgentConfig config,string configFile)
{
    public string Status {get;private set;}="Connecting";
    public async Task Run(CancellationToken ct)
    {
        monitor.Start();int failures=0;
        while(config.HostId==""&&!ct.IsCancellationRequested)
        {
            try
            {
                using var client=FleetFiles.Client(config.Connection);
                using var response=await client.PostAsJsonAsync("/fleet/v1/enroll",new Enrollment(config.PairingCode,config.InstallationId,config.Name,config.Connection.NetworkType),ct);response.EnsureSuccessStatusCode();
                var result=await response.Content.ReadFromJsonAsync<EnrollmentResult>(ct)??throw new InvalidDataException();
                config.HostId=result.HostId;config.Connection.Token=result.Token;config.HubEpoch=result.HubId;config.PairingCode="";FleetFiles.Write(configFile,config);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
            catch(Exception e){Status="Pairing unavailable · collecting locally · "+e.GetType().Name;Paths.Log("fleet-pairing",e);try{await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,Math.Pow(2,Math.Min(++failures,6)))),ct);}catch(OperationCanceledException){return;}}
        }
        await Task.WhenAll(LiveLoop(ct),HistoryLoop(ct));
    }
    async Task LiveLoop(CancellationToken ct)
    {
        using var client=FleetFiles.Client(config.Connection);client.Timeout=TimeSpan.FromSeconds(8);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var response=await client.PostAsJsonAsync("/fleet/v1/live",monitor.Snapshot() with{Name=config.Name},ct);response.EnsureSuccessStatusCode();
                Status=monitor.Store.QueueInfo().Count>0?"Connected · history syncing":"Connected · up to date";
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e){Status="Disconnected · collecting locally · "+e.GetType().Name;Paths.Log("fleet-live",e);}
            try{FleetFiles.Write(Path.Combine(Path.GetDirectoryName(configFile)!,"sync-status.json"),new{Status,At=J.Now,Count=monitor.Store.QueueInfo().Count,Bytes=monitor.Store.QueueInfo().Bytes});await Task.Delay(2000,ct);}catch(OperationCanceledException){}catch(Exception e){Paths.Log("fleet-status",e);}
        }
    }
    async Task HistoryLoop(CancellationToken ct)
    {
        using var client=FleetFiles.Client(config.Connection);int failures=0;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var events=monitor.Store.Events();
                if(events.Count==0){await Task.Delay(1000,ct);continue;}
                using var response=await client.PostAsJsonAsync("/fleet/v1/ingest",new IngestBatch(config.InstallationId,events),ct);response.EnsureSuccessStatusCode();
                var ack=await response.Content.ReadFromJsonAsync<IngestAck>(ct)??throw new InvalidDataException();
                if(ack.HubId!=config.HubEpoch)throw new InvalidDataException("Hub identity changed; re-pair required");
                var offered=events.Select(e=>e.Sequence).ToHashSet();if(ack.Accepted.Any(s=>!offered.Contains(s)))throw new InvalidDataException("Invalid acknowledgement");
                monitor.Store.Acknowledge(ack.Accepted);failures=0;
                // Bound historical payload delivery to about 1 MiB/s, independently of live updates.
                await Task.Delay(Math.Max(100,events.Sum(e=>System.Text.Encoding.UTF8.GetByteCount(e.Payload))/1024),ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e){Paths.Log("fleet-history",e);failures=Math.Min(failures+1,6);try{await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,Math.Pow(2,failures))+Random.Shared.NextDouble()),ct);}catch(OperationCanceledException){}}
        }
    }

}
public static class FleetRuntime
{
    public static int Run(string role)
    {
        using var instance=new FileStream(Path.Combine(Paths.Root,"service.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var builder=Host.CreateApplicationBuilder([]);builder.Logging.ClearProviders();builder.Services.AddWindowsService(o=>o.ServiceName=role=="hub"?"AIPULSEHub":"AIPULSEAgent");
        builder.Services.AddSingleton(new FleetRole(role));builder.Services.AddHostedService<FleetWorker>();
        using var host=builder.Build();host.Run();return Environment.ExitCode;
    }
    sealed record FleetRole(string Role);
    sealed class FleetWorker(FleetRole role,IHostApplicationLifetime lifetime):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if(role.Role=="hub"){using var hub=new FleetHub(Paths.Root);await hub.Run(stoppingToken);}
                else
                {
                    string file=Path.Combine(Paths.Root,"agent.json");var config=FleetFiles.Read<HostAgentConfig>(file);var settings=Paths.Load();settings.UsageRoots=config.UsageRoots;settings.ReadAgentUsage=config.UsageRoots.Count>0;
                    using var monitor=new MonitorService(settings,replication:true);await new FleetAgent(monitor,config,file).Run(stoppingToken);
                }
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
            catch(Exception e){Paths.Log("fleet-service",e);Environment.ExitCode=1;lifetime.StopApplication();}
        }
    }
}
