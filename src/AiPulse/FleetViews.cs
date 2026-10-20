using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using System.Net.Http.Json;

namespace AiPulse;

public sealed partial class MainWindow
{
    string fleetNotice="";
    void InitializeFleet()
    {
        if(!localService.Settings.FleetDashboard||localService.Settings.FleetConnectionFile is not string file)return;
        try{fleet=new(FleetFiles.Read<FleetConnection>(file)){SelectedHost=localService.Settings.SelectedHostId};fleet.Start();}catch(Exception e){Paths.Log("fleet-dashboard",e);fleetNotice="Hub connection could not be loaded. Open Settings.";}
    }
    string SelectedHostLabel()=>fleet?.Hosts.FirstOrDefault(h=>h.Id==fleet.SelectedHost) is {} h?h.Name+" · "+h.Status:fleet?.SelectedHost==""?"All hosts":"Waiting for selected host";
    string lastHostMenu="";
    void AddFleetHeader(Grid title,bool compact)
    {
        if(fleet==null)return;
        var choices=new List<string>{"All hosts"};choices.AddRange(fleet.Hosts.Select(h=>h.Name+" · "+h.Status));
        var ids=new[]{""}.Concat(fleet.Hosts.Select(h=>h.Id)).ToArray();
        int selected=Array.IndexOf(ids,fleet.SelectedHost);
        if(selected<0){choices.Add("Selected host · waiting");ids=ids.Append(fleet.SelectedHost).ToArray();selected=ids.Length-1;}
        var selector=new ComboBox{ItemsSource=choices,SelectedIndex=selected,MinHeight=compact?25:34,FontSize=compact?11:12,HorizontalAlignment=compact?HorizontalAlignment.Stretch:HorizontalAlignment.Center,MaxWidth=compact?double.PositiveInfinity:300,MinWidth=compact?0:180};
        AutomationProperties.SetName(selector,"Select monitored host");
        selector.SelectionChanged+=(_,_)=>{if(selector.SelectedIndex<0||selector.SelectedIndex>=ids.Length)return;fleet.SelectedHost=ids[selector.SelectedIndex];localService.Settings.SelectedHostId=fleet.SelectedHost;Paths.Save(localService.Settings);modelsExpanded=false;layoutPage=null;scroller=null;RenderBody();};
        if(compact){title.RowDefinitions=new("Auto,28");Grid.SetRow(selector,1);Grid.SetColumnSpan(selector,2);}else Grid.SetColumnSpan(selector,2);
        title.Children.Add(selector);lastHostMenu=string.Join('|',choices);
    }
    void RefreshHostMenu()
    {if(fleet!=null&&lastHostMenu!=string.Join('|',new[]{"All hosts"}.Concat(fleet.Hosts.Select(h=>h.Name+" · "+h.Status))))BuildHeader();}
    Control FleetCompact()
    {
        var s=new StackPanel{Margin=new Thickness(12),Spacing=8};s.Children.Add(Text("FLEET",12,null,true));
        s.Children.Add(Text($"{fleet!.Hosts.Count(h=>h.Status=="Live")} live / {fleet.Hosts.Count} hosts",18,null,true));
        s.Children.Add(Text("Choose a host for hardware, agents and today's tokens.",11,"#8ba0bd"));return s;
    }
    Control FleetOverview()
    {
        var s=new StackPanel{Spacing=16};s.Children.Add(Heading("All hosts","Central monitoring · "+fleet!.Status));
        var hosts=fleet.Hosts;s.Children.Add(Text($"{hosts.Count(h=>h.Status=="Live")} live · {hosts.Count(h=>h.Status=="Stale")} stale · {hosts.Count(h=>h.Status=="Offline")} offline",17,null,true));
        s.Children.Add(Text("Select a host above to open its Overview, hardware, sensors, models, agents, tokens and history. Percentages and temperatures belong to each host and are not added together.",12,"#8ba0bd"));
        foreach(var h in hosts)
        {
            var card=new StackPanel{Spacing=8};card.Children.Add(Text(h.Name+" · "+h.Status,17,null,true));card.Children.Add(Text(h.Platform+" · "+h.NetworkType,12,"#8ba0bd"));
            card.Children.Add(Text(h.ReceivedAt>0?"Last contact "+DateTimeOffset.FromUnixTimeSeconds(h.ReceivedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"):"Waiting for first agent connection",12));
            card.Children.Add(Text($"Pending upload: {h.QueueCount:N0} records · {h.QueueBytes/1048576.0:0.0} MiB",12));
            var open=Btn("Monitor "+h.Name);open.Click+=(_,_)=>{fleet.SelectedHost=h.Id;localService.Settings.SelectedHostId=h.Id;Paths.Save(localService.Settings);page="Overview";BuildHeader();RenderBody();};card.Children.Add(open);s.Children.Add(Card(card));
        }
        if(hosts.Count==0)s.Children.Add(Message("No hosts paired","Open Settings to create a pairing file, then install the Host Agent on another computer."));
        return s;
    }
    void RequestServiceInstall(string role,string folder,bool remove=false)
    {
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true};
        if(OperatingSystem.IsWindows())info.Verb="runas";
        else{info=new ProcessStartInfo("pkexec"){UseShellExecute=false};info.ArgumentList.Add(Environment.ProcessPath!);}
        foreach(var arg in new[]{remove?"--remove-fleet":"--install-fleet",role,"--source-config",folder,"--data-dir",Path.Combine(folder,"install-log")})info.ArgumentList.Add(arg);
        Process.Start(info);fleetNotice="Service installation requested. Approve the operating-system administrator prompt. The service starts at boot; check its status below before restarting the dashboard.";
    }
    Control FleetSettingsPanel()
    {
        var s=new StackPanel{Spacing=12};s.Children.Add(Text("Central dashboard & Host Agents",19,null,true));
        s.Children.Add(Text("Hub service: "+FleetInstaller.Status("hub")+" · Host Agent: "+FleetInstaller.Status("agent"),12,null,true));var refresh=Btn("Refresh service status");refresh.Click+=(_,_)=>RenderBody();s.Children.Add(refresh);
        s.Children.Add(Text("Create the main hub once. Pair each monitored computer, including this one if needed. Host Agents collect before sign-in and buffer while the hub is unavailable.",12,"#a1b2c9"));
        var hostName=new TextBox{Text=Environment.MachineName,PlaceholderText="Main hub DNS name or IP address"};s.Children.Add(Text("Main hub address · reachable by your other computers",13,null,true));s.Children.Add(hostName);
        var create=Btn("1. Set up this computer as the main hub");
        create.Click+=(_,_)=>{try{string folder=Path.Combine(Paths.Root,"fleet-setup","hub");var connection=FleetFiles.InitializeHub(folder,hostName.Text?.Trim()??"localhost");string connectionFile=Path.Combine(folder,"dashboard-connection.json");localService.Settings.FleetConnectionFile=connectionFile;Paths.Save(localService.Settings);RequestServiceInstall("hub",folder);}catch(Exception e){fleetNotice="Hub setup failed: "+e.Message;}RenderBody();};s.Children.Add(create);
        var network=new ComboBox{ItemsSource=new[]{"Private LAN","VPN","Internet"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};s.Children.Add(Text("Network type",13,null,true));s.Children.Add(network);
        var url=new TextBox{PlaceholderText="https://main-computer:11440"};var fingerprint=new TextBox{PlaceholderText="SHA-256 certificate fingerprint (LAN / VPN)"};var token=new TextBox{PlaceholderText="Dashboard access token",PasswordChar='●'};
        FleetConnection? existing=null;try{if(localService.Settings.FleetConnectionFile is {} file)existing=FleetFiles.Read<FleetConnection>(file);}catch{}
        if(existing!=null){url.Text=existing.Url;fingerprint.Text=existing.CertificateSha256;token.Text=existing.Token;network.SelectedIndex=Array.IndexOf(new[]{"Private LAN","VPN","Internet"},existing.NetworkType);}
        s.Children.Add(url);s.Children.Add(fingerprint);s.Children.Add(token);
        var connect=Btn("2. Save dashboard connection");connect.Click+=async(_,_)=>
        {
            try
            {
                var connection=new FleetConnection{Url=url.Text?.Trim()??"",CertificateSha256=fingerprint.Text?.Trim()??"",Token=token.Text?.Trim()??"",NetworkType=network.SelectedItem?.ToString()??"Private LAN"};
                using var client=FleetFiles.Client(connection);_ = await client.GetFromJsonAsync<List<HostSummary>>("/fleet/v1/hosts");
                string file=Path.Combine(Paths.Root,"fleet-dashboard.json");FleetFiles.Write(file,connection);localService.Settings.FleetConnectionFile=file;localService.Settings.FleetDashboard=true;Paths.Save(localService.Settings);
                fleetNotice="Hub connection verified. Restart AIPULSE to use the central dashboard and stop duplicate desktop collection. The hub and agents keep running.";
            }catch(Exception e){fleetNotice="Connection failed: "+e.GetType().Name+". Check the service, address, certificate and access token.";}RenderBody();
        };s.Children.Add(connect);
        var standalone=Btn("Use standalone local monitoring after restart");standalone.Click+=(_,_)=>{localService.Settings.FleetDashboard=false;Paths.Save(localService.Settings);fleetNotice="Standalone mode saved. Restart the dashboard to apply.";RenderBody();};s.Children.Add(standalone);
        s.Children.Add(Text("Add a monitored computer",16,null,true));
        var name=new TextBox{Text=Environment.MachineName,PlaceholderText="Name of monitored computer"};s.Children.Add(name);
        var pair=Btn("Create a 10-minute Host Agent pairing file");pair.Click+=async(_,_)=>
        {
            try
            {
                var connection=FleetFiles.Read<FleetConnection>(localService.Settings.FleetConnectionFile??"");using var client=FleetFiles.Client(connection);
                using var response=await client.PostAsJsonAsync("/fleet/v1/pair",new{});response.EnsureSuccessStatusCode();var ticket=await response.Content.ReadFromJsonAsync<PairingTicket>()??throw new InvalidDataException();
                var config=new HostAgentConfig{Name=name.Text?.Trim()??Environment.MachineName,PairingCode=ticket.Code,Connection=new(){Url=connection.Url,CertificateSha256=connection.CertificateSha256,NetworkType=network.SelectedItem?.ToString()??"Private LAN"}};
                string file=Path.Combine(Paths.Root,"exports","host-agent-pairing-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json");FleetFiles.Write(file,config);fleetNotice="Pairing file created: "+file+". Transfer it privately to the monitored computer and import it below. It expires in ten minutes and works once.";
            }catch(Exception e){fleetNotice="Pairing failed: "+e.GetType().Name;}RenderBody();
        };s.Children.Add(pair);
        var usage=new CheckBox{Content="Allow this Host Agent to read this user's Codex / Claude Code token metadata",IsChecked=false};s.Children.Add(usage);
        s.Children.Add(Text("Opting in configures only the two supported metadata folders for this user. On Ubuntu, the service account also needs read access to those folders. Protected sensors and other users' metadata may remain unavailable.",11,"#8ba0bd"));
        var install=Btn("Import pairing file & install Host Agent on this computer");install.Click+=async(_,_)=>
        {
            try
            {
                var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Choose Host Agent pairing file",AllowMultiple=false,FileTypeFilter=[new("JSON pairing file"){Patterns=["*.json"]}]});if(files.Count==0)return;
                string file=files[0].TryGetLocalPath()??throw new InvalidDataException("Choose a local file");var config=FleetFiles.Read<HostAgentConfig>(file);FleetFiles.ValidateConnection(config.Connection);
                if(config.PairingCode.Length==0||config.Connection.Token.Length>0)throw new InvalidDataException("Use a fresh pairing file");
                config.InstallationId=Guid.NewGuid().ToString("N");
                if(usage.IsChecked==true){string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);config.UsageRoots=[Path.Combine(home,".codex","sessions"),Path.Combine(home,".claude","projects")];}
                string folder=Path.Combine(Paths.Root,"fleet-setup","agent");FleetFiles.Write(Path.Combine(folder,"agent.json"),config);FleetFiles.Write(Path.Combine(folder,"settings.json"),new AppSettings{ReadAgentUsage=config.UsageRoots.Count>0,UsageRoots=config.UsageRoots});RequestServiceInstall("agent",folder);
            }catch(Exception e){fleetNotice="Host Agent setup failed: "+e.Message;}RenderBody();
        };s.Children.Add(install);
        s.Children.Add(Text("Private LAN/VPN verify the hub fingerprint. Internet uses publicly trusted HTTPS; configure a reachable hostname and certificate on the hub first. Network selection does not open router ports or install a VPN.",11,"#8ba0bd"));
        foreach(string role in new[]{"hub","agent"}){var remove=Btn("Stop and remove local "+role+" service · keep data");remove.Click+=(_,_)=>{try{RequestServiceInstall(role,Path.Combine(Paths.Root,"fleet-setup",role),true);}catch(Exception e){fleetNotice=e.Message;}RenderBody();};s.Children.Add(remove);}
        if(fleetNotice!="")s.Children.Add(Message("Central monitoring setup",fleetNotice));return Card(s);
    }
}
