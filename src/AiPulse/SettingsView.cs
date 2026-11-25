using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using System.Diagnostics;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Interactivity;

namespace AiPulse;

// Settings page: collectors, retention, backups, restore and quit.
public sealed partial class MainWindow
{
    Control SettingsPage()
    {
        var service=localService;
        var stack=new StackPanel{Spacing=15};stack.Children.Add(Heading("Settings","Local collection and central monitoring connections"));stack.Children.Add(FleetSettingsPanel());stack.Children.Add(Text("Standalone settings below apply to this desktop. Central history retention is configured in the hub service; host policies remain explicit.",12,"#8ba0bd"));
        stack.Children.Add(Card(AssistantSettingsPanel()));
        if(OperatingSystem.IsWindows()){var sensors=Btn("Connect deep sensors");sensors.Click+=(_,_)=>{service.SensorBridge.Enable();notice=service.SensorBridge.Status;RenderBody();};stack.Children.Add(sensors);settingsSensorStatus=Text(SensorDescription(),12,"#8ea3c0");stack.Children.Add(settingsSensorStatus);}
        var sensorStartup=new CheckBox{Content="Ask for deep-sensor permission when AIPULSE starts",IsChecked=service.Settings.RequestSensorsAtLaunch,IsVisible=OperatingSystem.IsWindows()};stack.Children.Add(sensorStartup);
        stack.Children.Add(Text("The isolated helper requires Windows administrator approval each session. GPU VR SoC and GPU core temperature are different measurements.",11,"#8ea3c0"));
        var local=new TextBox{Text=service.Settings.OllamaUrl,PlaceholderText="http://127.0.0.1:11434"};stack.Children.Add(Text("Local Ollama server",13,null,true));stack.Children.Add(local);
        var remote=new TextBox{Text=string.Join('\n',service.Settings.RemoteEndpoints.Select(e=>e.Name+" | "+e.Url)),AcceptsReturn=true,MinHeight=74,PlaceholderText="ai-node-01 | http://server:11434"};stack.Children.Add(Text("Remote Ollama / future k3s endpoints · one Name | URL per line",13,null,true));stack.Children.Add(remote);
        stack.Children.Add(Text("Remote hardware and Kubernetes pod metrics require a future cluster collector. These endpoints currently discover remote Ollama models.",11,"#8ea3c0"));
        var agents=new CheckBox{Content="Read local Codex / Claude Code usage metadata",IsChecked=service.Settings.ReadAgentUsage};stack.Children.Add(agents);
        var start=new CheckBox{Content="Launch AI Pulse at sign-in",IsChecked=service.Settings.StartAtLogin};stack.Children.Add(start);
        var motion=new CheckBox{Content="Reduce motion (no page, button or bar animation; applies after restart for buttons and bars)",IsChecked=localService.Settings.ReduceMotion};stack.Children.Add(motion);
        var retention=new NumericUpDown{Value=service.Settings.RetentionDays,Minimum=7,Maximum=365,Increment=1};stack.Children.Add(Text("History retention · 7–365 days (shipping default: 90)",13,null,true));stack.Children.Add(retention);
        stack.Children.Add(Text("Detailed samples stay for 7 days. Longer retention keeps minute summaries, usage and sessions. Increasing this cannot recover deleted hardware history; available local agent records are re-imported. Reducing it removes older records at the next hourly cleanup.",11,"#8ea3c0"));
        var dailyBackups=new NumericUpDown{Value=service.Settings.BackupPolicy.DailyCopies,Minimum=1,Maximum=14,Increment=1};stack.Children.Add(Text("Daily backup restore points · 1–14",13,null,true));stack.Children.Add(dailyBackups);
        var weeklyBackups=new NumericUpDown{Value=service.Settings.BackupPolicy.WeeklyCopies,Minimum=0,Maximum=12,Increment=1};stack.Children.Add(Text("Weekly backup restore points · 0–12",13,null,true));stack.Children.Add(weeklyBackups);
        var backupBudget=new NumericUpDown{Value=service.Settings.BackupPolicy.BudgetMiB,Minimum=64,Maximum=8192,Increment=64};stack.Children.Add(Text("Backup storage budget · MiB",13,null,true));stack.Children.Add(backupBudget);
        Avalonia.Automation.AutomationProperties.SetName(dailyBackups,"Daily backup restore points");Avalonia.Automation.AutomationProperties.SetName(weeklyBackups,"Weekly backup restore points");Avalonia.Automation.AutomationProperties.SetName(backupBudget,"Backup storage budget in MiB");
        stack.Children.Add(Text("Backups are compacted and compressed. The storage budget can reduce the number of older restore points; the latest verified backup is always kept even if it exceeds the budget. This does not change your monitoring-history retention.",11,"#8ea3c0"));
        var temp=new NumericUpDown{Value=(decimal)service.Settings.TemperatureWarning,Minimum=50,Maximum=120,Increment=1};stack.Children.Add(Text("Sustained temperature warning (°C) · applies after 30 seconds",13));stack.Children.Add(temp);
        var tokenBudget=new NumericUpDown{Value=service.Settings.DailyTokenBudget,Minimum=0,Maximum=100_000_000_000,Increment=100_000,FormatString="N0"};stack.Children.Add(Text("Daily token budget across all sources · 0 is off",13));stack.Children.Add(tokenBudget);
        var costBudget=new NumericUpDown{Value=(decimal)service.Settings.DailyCostBudgetUsd,Minimum=0,Maximum=10_000,Increment=1,FormatString="0.00"};stack.Children.Add(Text("Daily Claude Code cost budget (US$) · 0 is off · uses the cost Claude Code reports over OTel",13));stack.Children.Add(costBudget);
        var apiPort=new NumericUpDown{Value=service.Settings.MetricsPilotPort,Minimum=0,Maximum=65535,Increment=1,FormatString="0"};stack.Children.Add(Text("Local metrics and lab API port · 0 is off · applies after restart",13));stack.Children.Add(apiPort);
        stack.Children.Add(Text("When on, http://127.0.0.1:<port>/metrics serves Prometheus metrics and POST /labs/start and /labs/stop let scripts mark lab runs. It listens on this PC only.",11,"#8ea3c0"));
        Avalonia.Automation.AutomationProperties.SetName(apiPort,"Local metrics and lab API port");
        Avalonia.Automation.AutomationProperties.SetName(tokenBudget,"Daily token budget");Avalonia.Automation.AutomationProperties.SetName(costBudget,"Daily Claude Code cost budget");
        var save=Btn("Save settings");save.Click+=(_,_)=>
        {
            try{Ollama.Validate(local.Text??"");var endpoints=new List<Endpoint>();foreach(var line in (remote.Text??"").Split('\n',StringSplitOptions.RemoveEmptyEntries)){var parts=line.Split('|',2);if(parts.Length!=2||string.IsNullOrWhiteSpace(parts[0]))throw new Exception("Use Name | URL for each remote endpoint.");var name=parts[0].Trim();if(name=="This PC"||endpoints.Any(e=>e.Name==name))throw new Exception("Host names must be unique.");Ollama.Validate(parts[1].Trim());endpoints.Add(new(name,parts[1].Trim()));}
            service.Settings.OllamaUrl=local.Text!.Trim();service.Settings.RemoteEndpoints=endpoints;service.Settings.ReadAgentUsage=agents.IsChecked==true;service.Settings.TemperatureWarning=(double)(temp.Value??90);service.Settings.DailyTokenBudget=(long)(tokenBudget.Value??0);service.Settings.MetricsPilotPort=(int)(apiPort.Value??0);service.Settings.DailyCostBudgetUsd=(double)(costBudget.Value??0);service.Settings.StartAtLogin=start.IsChecked==true;localService.Settings.ReduceMotion=motion.IsChecked==true;Motion.Reduced=localService.Settings.ReduceMotion;service.Settings.RequestSensorsAtLaunch=sensorStartup.IsChecked==true;service.Agents.SetRetention((int)(retention.Value??90));service.Settings.RetentionDays=service.Store.RetentionDays;service.Settings.BackupDailyCopies=(int)(dailyBackups.Value??3);service.Settings.BackupWeeklyCopies=(int)(weeklyBackups.Value??4);service.Settings.BackupBudgetMiB=(int)(backupBudget.Value??512);Startup.Configure(service.Settings.StartAtLogin);Paths.Save(localService.Settings);notice="Settings saved.";}catch(Exception e){notice=e.Message;}RenderBody();
        };stack.Children.Add(save);
        stack.Children.Add(Message("Ollama observation gateway",service.GatewayStatus+"\nFor exact request tokens, point an Ollama-compatible client to http://127.0.0.1:"+service.Settings.GatewayPort+". Calls directly to Ollama are not counted."));
        stack.Children.Add(Message("Usage collection framework",$"OTLP/HTTP: http://127.0.0.1:{service.Settings.TelemetryPort} · {service.Telemetry.Status}\nAccepts native Claude Code events, GenAI traces and server counters. Setup profiles are included in docs/telemetry. No prompts, answers, tool arguments or credentials are retained.\nPersistent default-port Ollama capture requires the optional relay setup and one Ollama restart. Source status and last observations appear on Token."));
        stack.Children.Add(Text("History & backup",17,null,true));stack.Children.Add(Text("Data folder: "+Paths.Root+"\nBackup folder: "+Paths.Backups(service.Settings)+"\nLast verified backup: "+(service.Settings.LastBackup??"Pending")+$"\nCompressed snapshots: {service.Settings.BackupPolicy.DailyCopies} daily + {service.Settings.BackupPolicy.WeeklyCopies} weekly, within {service.Settings.BackupPolicy.BudgetMiB} MiB where possible. Local copies do not protect against drive failure.",12,"#a1b2c9"));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};var backup=Btn("Back up now");backup.Click+=async(_,_)=>{backup.IsEnabled=false;notice=await service.BackupNow() is string f?"Verified backup: "+Path.GetFileName(f):"Backup failed. Check available space.";RenderBody();};actions.Children.Add(backup);var folder=Btn("Open data folder");folder.Click+=(_,_)=>OpenFolder(Paths.Root);actions.Children.Add(folder);stack.Children.Add(actions);
        var optimize=Btn("Optimize existing backups");optimize.Click+=async(_,_)=>{optimize.IsEnabled=false;try{var result=await Task.Run(()=>service.Store.OptimizeStoredBackups(Paths.Backups(service.Settings),service.Settings.BackupPolicy));notice=$"Backups verified and compressed: {result.Before/1048576.0:0.0} → {result.After/1048576.0:0.0} MiB. Cloud-sync quota may take time to update.";}catch(Exception e){Paths.Log("backup-optimize",e);notice="Backup optimization did not finish. Original restore points are kept until their compressed copies are verified.";}RenderBody();};stack.Children.Add(optimize);
        var restore=Btn("Restore a verified backup…");restore.Click+=async(_,_)=>
        {
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="Choose an AI Pulse backup",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("AI Pulse backup"){Patterns=["*.db.gz","*.db"]}]});
            if(files.Count==0)return;string? file=files[0].TryGetLocalPath();if(file==null)return;
            try
            {
                await Task.Run(()=>Storage.VerifyBackup(file));
                var dialog=new Window{Title="Restore AI Pulse history",Width=460,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Canvas};
                var contents=new StackPanel{Margin=new Thickness(22),Spacing=18};contents.Children.Add(Text("Restore this snapshot?",18,null,true));contents.Children.Add(Text("AI Pulse will restart. Your current database will be preserved as a rollback copy.",13));var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};var yes=Btn("Restore and restart");yes.Click+=(_,_)=>dialog.Close(true);var no=Btn("Cancel");no.Click+=(_,_)=>dialog.Close(false);buttons.Children.Add(yes);buttons.Children.Add(no);contents.Children.Add(buttons);dialog.Content=contents;
                if(await dialog.ShowDialog<bool>(this))
                {
                    var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,ArgumentList={"--restore",file,"--data-dir",Paths.Root,"--wait-for-exit",Environment.ProcessId.ToString(),"--launch-after"}};Process.Start(info);
                    if(Application.Current is App app){app.Quitting=true;(app.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();}
                }
            }catch(Exception e){notice="Restore was not started: "+e.Message;RenderBody();}
        };stack.Children.Add(restore);
        var quit=Btn("Quit AI Pulse · Ctrl+Q");quit.Click+=(_,_)=>Quit();stack.Children.Add(quit);
        if(notice!="")stack.Children.Add(Message("Status",notice));stack.Children.Add(Text("AI Pulse "+typeof(MainWindow).Assembly.GetName().Version?.ToString(3)+" · local-first · Avalonia / SQLite / LibreHardwareMonitor\nHardware coverage depends on your device and operating system.",11,"#7186a2"));return stack;
    }
}
