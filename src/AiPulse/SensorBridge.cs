using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiPulse;

// The elevated process only collects hardware readings and writes them to a same-user pipe.
// It never accepts commands, URLs, file paths or device writes from the UI.
public sealed class SensorBridge : IDisposable
{
    readonly CancellationTokenSource stop=new();
    public List<Reading> Readings {get;private set;}=[];
    public long LastSeen {get;private set;}
    public string Status{get;private set;}="Standard sensor access";
    Task? receiver;
    public void Enable()
    {
        if(!OperatingSystem.IsWindows()||receiver is {IsCompleted:false})return;
        string pipe="ai-pulse-sensors-"+Guid.NewGuid().ToString("N");
        // CurrentUserOnly also compares elevation levels on Windows. Use an explicit
        // user SID ACL and verify both process IDs so a medium-integrity UI can receive
        // readings from its own elevated child without admitting other users/processes.
        var security=new PipeSecurity();var sid=WindowsIdentity.GetCurrent().User!;
        security.SetAccessRuleProtection(true,false);security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        var server=NamedPipeServerStreamAcl.Create(pipe,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,0,0,security);
        Process helper;
        try
        {
            var exe=Environment.ProcessPath!;
            if(Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Use the packaged executable for sensor access.");
            helper=Process.Start(new ProcessStartInfo(exe){Arguments=$"--sensor-helper --pipe {pipe} --parent {Environment.ProcessId}",UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden})??throw new InvalidOperationException("Sensor helper did not start.");
            Status="Waiting for sensor helper";
        }
        catch(Exception e){server.Dispose();Paths.Log("sensor-elevation",e);Status="Sensor permission not granted";return;}
        receiver=Task.Run(async()=>
        {
            using(server)
            {
                try
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromMinutes(2));await server.WaitForConnectionAsync(timeout.Token);
                    if(!GetNamedPipeClientProcessId(server.SafePipeHandle,out uint childId)||childId!=helper.Id)throw new UnauthorizedAccessException("Unexpected sensor peer.");
                    using var reader=new StreamReader(server);
                    while(!stop.IsCancellationRequested)
                    {var line=await reader.ReadLineAsync(stop.Token);if(line==null)break;if(line.Length>2_000_000)break;var rows=JsonSerializer.Deserialize<List<Reading>>(line);if(rows!=null){Readings=rows;LastSeen=J.Now;Status="Deep sensor access connected";}}
                }
                catch(Exception e){if(!stop.IsCancellationRequested)Paths.Log("sensor-pipe",e);}
                Status="Sensor helper disconnected";helper.Dispose();
            }
        });
    }
    public static int Run(string[] args)
    {
        string Arg(string key){var i=Array.IndexOf(args,key);return i>=0&&args.Length>i+1?args[i+1]:"";}
        string pipe=Arg("--pipe");if(!pipe.StartsWith("ai-pulse-sensors-",StringComparison.Ordinal)||!int.TryParse(Arg("--parent"),out var pid))return 1;
        try
        {
            using var parent=Process.GetProcessById(pid);
            using var client=new NamedPipeClientStream(".",pipe,PipeDirection.Out,PipeOptions.None,TokenImpersonationLevel.Anonymous);
            client.Connect(15000);if(!GetNamedPipeServerProcessId(client.SafePipeHandle,out uint serverId)||serverId!=pid)return 2;
            using var writer=new StreamWriter(client){AutoFlush=true};using var hardware=new Hardware();
            while(!parent.HasExited&&client.IsConnected){var rows=hardware.Sample().Where(r=>r.Id.StartsWith("sensor")||r.Id is "cpu.temperature" or "gpu.temperature" or "gpu.power").ToList();writer.WriteLine(JsonSerializer.Serialize(rows));Thread.Sleep(2000);}return 0;
        }catch{return 1;}
    }
    public void Dispose(){stop.Cancel();try{receiver?.Wait(1000);}catch{}stop.Dispose();}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
}
