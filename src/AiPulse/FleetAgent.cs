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
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var client=FleetFiles.Client(config.Connection);
                if(config.HostId=="")
                {
                    using var response=await client.PostAsJsonAsync("/fleet/v1/enroll",new Enrollment(config.PairingCode,config.InstallationId,config.Name,config.Connection.NetworkType),ct);response.EnsureSuccessStatusCode();
                    var result=await response.Content.ReadFromJsonAsync<EnrollmentResult>(ct)??throw new InvalidDataException();
                    config.HostId=result.HostId;config.Connection.Token=result.Token;config.HubEpoch=result.HubId;config.PairingCode="";FleetFiles.Write(configFile,config);
                    client.DefaultRequestHeaders.Authorization=new("Bearer",result.Token);
                }
                // The live lane is independent of history upload. A backlog cannot masquerade as live data.
                using(var live=await client.PostAsJsonAsync("/fleet/v1/live",monitor.Snapshot() with{Name=config.Name},ct))live.EnsureSuccessStatusCode();
                for(int round=0;round<4;round++)
                {
                    var events=monitor.Store.Events();if(events.Count==0)break;
                    using var response=await client.PostAsJsonAsync("/fleet/v1/ingest",new IngestBatch(config.InstallationId,events),ct);response.EnsureSuccessStatusCode();
                    var ack=await response.Content.ReadFromJsonAsync<IngestAck>(ct)??throw new InvalidDataException();
                    if(ack.HubId!=config.HubEpoch)throw new InvalidDataException("Hub identity changed; re-pair required");
                    var offered=events.Select(e=>e.Sequence).ToHashSet();if(ack.Accepted.Any(s=>!offered.Contains(s)))throw new InvalidDataException("Invalid acknowledgement");
                    monitor.Store.Acknowledge(ack.Accepted);
                    if(ack.Accepted.Count==0)break;
                }
                failures=0;Status=monitor.Store.QueueInfo().Count>0?"Connected · history syncing":"Connected · up to date";
                FleetFiles.Write(Path.Combine(Path.GetDirectoryName(configFile)!,"sync-status.json"),new{Status,At=J.Now,Queue=monitor.Store.QueueInfo()});
                await Task.Delay(2000,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception e)
            {
                failures=Math.Min(failures+1,6);Status="Disconnected · collecting locally · "+e.GetType().Name;Paths.Log("fleet-sync",e);
                FleetFiles.Write(Path.Combine(Path.GetDirectoryName(configFile)!,"sync-status.json"),new{Status,At=J.Now,Queue=monitor.Store.QueueInfo()});
                try{await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,Math.Pow(2,failures))+Random.Shared.NextDouble()),ct);}catch(OperationCanceledException){}
            }
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
