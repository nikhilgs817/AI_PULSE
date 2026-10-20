using System.Net.Http.Json;

namespace AiPulse;

public static class FleetSetup
{
    public static async Task CreateAgent(string connectionFile,string destination,bool includeUserMetadata=false)
    {
        var connection=FleetFiles.Read<FleetConnection>(connectionFile);using var client=FleetFiles.Client(connection);
        PairingTicket? ticket=null;
        for(int i=0;i<20;i++)
        {
            try{using var response=await client.PostAsJsonAsync("/fleet/v1/pair",new{});response.EnsureSuccessStatusCode();ticket=await response.Content.ReadFromJsonAsync<PairingTicket>()??throw new InvalidDataException();break;}
            catch(HttpRequestException)when(i<19){await Task.Delay(500);}
        }
        if(ticket==null)throw new InvalidOperationException("Hub did not become ready");
        string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agent=new HostAgentConfig{Name=Environment.MachineName,PairingCode=ticket.Code,Connection=new(){Url=connection.Url,NetworkType=connection.NetworkType,CertificateSha256=connection.CertificateSha256},UsageRoots=includeUserMetadata?[Path.Combine(home,".codex","sessions"),Path.Combine(home,".claude","projects")]:[]};
        FleetFiles.Write(Path.Combine(destination,"agent.json"),agent);FleetFiles.Write(Path.Combine(destination,"settings.json"),new AppSettings{ReadAgentUsage=includeUserMetadata,UsageRoots=agent.UsageRoots,EnableGateway=includeUserMetadata});
    }
    public static async Task Check(string connectionFile)
    {
        using var client=FleetFiles.Client(FleetFiles.Read<FleetConnection>(connectionFile));
        for(int i=0;i<30;i++)
        {
            List<HostSummary> hosts=[];try{hosts=await client.GetFromJsonAsync<List<HostSummary>>("/fleet/v1/hosts")??[];}catch(HttpRequestException)when(i<29){}
            if(hosts.Any(h=>h.Status=="Live")){FleetFiles.Write(Path.Combine(Paths.Root,"fleet-check.json"),hosts);return;}
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("No live Host Agent reached the hub");
    }
}
