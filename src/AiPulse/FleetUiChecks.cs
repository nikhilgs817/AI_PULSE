using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Text.Json;
using System.Net;

namespace AiPulse;

public sealed partial class MainWindow
{
    async Task RunFleetUiChecks(List<string> results)
    {
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);results.Add("PASS: "+name);}
        string root=Path.Combine(Paths.Root,"fleet-ui");Directory.CreateDirectory(root);var connection=FleetFiles.InitializeHub(root);
        using var socket=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);socket.Start();int port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        var config=FleetFiles.Read<HubConfig>(Path.Combine(root,"hub.json"));config.Port=port;config.ListenAddress="127.0.0.1";FleetFiles.Write(Path.Combine(root,"hub.json"),config);connection.Url="https://localhost:"+port;
        using var hub=new FleetHub(root);using var stop=new CancellationTokenSource();var task=hub.Run(stop.Token);
        try
        {
            long now=J.Now;
            var a=hub.Enroll(new(hub.Pair().Code,Guid.NewGuid().ToString("N"),"Minisforum · fixture","Private LAN"));var b=hub.Enroll(new(hub.Pair().Code,Guid.NewGuid().ToString("N"),"Ubuntu node · fixture","VPN"));
            foreach(var pair in new[]{(a,31.0,11L),(b,74.0,63L)})
            {
                var host=hub.AuthenticateHost(pair.Item1.Token)!;
                var snapshot=new HostSnapshot{Name=host.Name,Platform=pair.Item1==a?"Windows":"Ubuntu",LogicalProcessors=4,SampledAt=now,Cpu="Fixture CPU",Gpu="Fixture GPU",ReadAgentUsage=true,ProcessSampleTime=DateTime.UtcNow,
                    Readings=[new("cpu.load","CPU","Fixture CPU","%",pair.Item2,"UI fixture"),new("ram.used","RAM","RAM","GiB",8,"UI fixture"),new("ram.total","Total","RAM","GiB",32,"UI fixture")],
                    ActiveRequests=new(){["fixture-model"]=2},Models=[new("This PC","fixture-model","Loaded",4,4,"Q4",4096,"")],ModelStatus=new(){[host.Name]="Connected"},
                    AgentApps=[new("Codex",[new(123,0,"codex",1,12,300)])]};hub.Live(host,snapshot);
                var events=new List<SyncEvent>{new(1,"sample",JsonSerializer.Serialize(new SamplePayload(now,snapshot.Readings))),new(2,"usage",JsonSerializer.Serialize(new Usage("identical-request","Ollama","fixture-model","identical-session",now,pair.Item3,2,0,0,0,null,null,"Completed"))),new(3,"session",JsonSerializer.Serialize(new SessionPayload("identical-session","Ollama","fixture-model","Working",now)))};
                hub.Ingest(host,new(host.InstallationId,events));
            }
            fleet=new(connection){SelectedHost=b.HostId};fleet.Start();
            for(int i=0;i<100&&(fleet.SelectedMonitor?.Store.Totals(now-1).Input!=63||fleet.SelectedMonitor?.RemoteSnapshot?.Name!="Ubuntu node · fixture");i++)await Task.Delay(100);
            localService.Settings.Compact=false;page="Overview";layoutPage=null;ApplyWindowLayout();RenderBody();await Task.Delay(200);
            var selector=this.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Select monitored host");
            Check(selector.ItemCount==3&&selector.SelectedIndex==2,"Fleet host dropdown includes All hosts and both paired hosts");
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Ubuntu node · fixture · Ubuntu · Fixture CPU")==true)&&service.LogicalProcessors==4,"Selected host supplies its own name, OS and CPU capacity instead of the dashboard machine");
            Check(service.Store.Totals(now-1).Input==63&&service.Ollama.Active.Values.Sum()==2&&ModelHost(service.Ollama.Models.Single())=="Ubuntu node · fixture"&&service.Ollama.Models.Count==1&&service.AgentApps.Single().MemoryMiB==300,"Selected-host model, token and process-resource sections share one host scope");
            using(var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96))){bitmap.Render(this);bitmap.Save(Path.Combine(Paths.Root,"fleet-selected-host.png"));}
            selector.SelectedIndex=1;for(int i=0;i<100&&(fleet.SelectedMonitor?.Store.Totals(now-1).Input!=11||fleet.SelectedMonitor?.RemoteSnapshot?.Name!="Minisforum · fixture");i++)await Task.Delay(100);RenderBody();
            Check(service.Store.Totals(now-1).Input==11&&service.Value("cpu.load")==31,"Switching hosts replaces the complete data scope without mixing identical record names");
            localService.Settings.Compact=true;localService.Settings.CompactVertical=true;ApplyWindowLayout();RenderBody();await Task.Delay(100);
            Check(Height==284&&header.Bounds.Height==62&&this.GetVisualDescendants().OfType<ComboBox>().Any(c=>AutomationProperties.GetName(c)=="Select monitored host"),"Compact fleet mode retains the host dropdown with a bounded additional row");
            using(var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96))){bitmap.Render(this);bitmap.Save(Path.Combine(Paths.Root,"fleet-compact.png"));}
            localService.Settings.Compact=false;fleet.SelectedHost="";ApplyWindowLayout();layoutPage=null;RenderBody();await Task.Delay(150);
            Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="All hosts")&&this.GetVisualDescendants().OfType<Button>().Count(b=>b.Content?.ToString()?.StartsWith("Monitor ")==true)==2,"All hosts shows fleet status and a drill-down for each host");
            using(var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96))){bitmap.Render(this);bitmap.Save(Path.Combine(Paths.Root,"fleet-all-hosts.png"));}
            page="Settings";layoutPage=null;RenderBody();await Task.Delay(150);Check(this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Central dashboard & Host Agents"),"Fleet setup provides hub, network profile, pairing and boot-agent installation controls");
        }
        finally{fleet?.Dispose();fleet=null;stop.Cancel();try{await task;}catch(OperationCanceledException){}localService.Settings.Compact=false;page="Overview";layoutPage=null;ApplyWindowLayout();RenderBody();}
    }
}
