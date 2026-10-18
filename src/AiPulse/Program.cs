using Avalonia;
using System.Text.Json;

namespace AiPulse;
public static class Program
{
    public static string[] Arguments=[];
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments=args;
        if(args.Contains("--sensor-helper"))return SensorBridge.Run(args);
        Paths.Initialize(args);
        int waitIndex=Array.IndexOf(args,"--wait-for-exit");if(waitIndex>=0&&args.Length>waitIndex+1&&int.TryParse(args[waitIndex+1],out var parentId)){try{System.Diagnostics.Process.GetProcessById(parentId).WaitForExit(20000);}catch(ArgumentException){}}
        try
        {
            if(args.Contains("--ui-test")&&!args.Contains("--data-dir"))throw new ArgumentException("UI checks require an isolated --data-dir.");
            bool exclusiveMode=!args.Contains("--diagnose")&&!args.Contains("--self-test");
            FileStream? acquired=null;
            if(exclusiveMode)
            {
                try{acquired=new FileStream(Path.Combine(Paths.Root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException) when(!args.Contains("--collect")&&!args.Contains("--restore"))
                {if(InstanceActivation.TryShow())return 0;throw;}
            }
            using var instance=acquired;
            if(args.Contains("--diagnose"))
            {
                using var h=new Hardware();h.Sample();Thread.Sleep(1500);var readings=h.Sample();
                var report=new{h.Status,h.CpuName,h.GpuName,Readings=readings};
                File.WriteAllText(Path.Combine(Paths.Root,"diagnostics.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return 0;
            }
            if(args.Contains("--self-test"))return SelfTests.Run();
            if(args.Contains("--restore"))
            {
                int index=Array.IndexOf(args,"--restore");if(args.Length<=index+1)throw new ArgumentException("Provide a backup file.");
                var backup=Path.GetFullPath(args[index+1]);Storage.VerifyBackup(backup);
                // Opening the DB exclusively protects against restoring while a collector is using it.
                using var exclusive=File.Exists(Paths.Database)?new FileStream(Paths.Database,FileMode.Open,FileAccess.ReadWrite,FileShare.None):null;
                string staged=Paths.Database+".restore";File.Copy(backup,staged,true);exclusive?.Dispose();
                if(File.Exists(Paths.Database))File.Move(Paths.Database,Paths.Database+".before-restore-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                File.Delete(Paths.Database+"-wal");File.Delete(Paths.Database+"-shm");File.Move(staged,Paths.Database);
                if(args.Contains("--launch-after")){instance?.Dispose();System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){ArgumentList={"--data-dir",Paths.Root},UseShellExecute=true});}return 0;
            }
            if(args.Contains("--collect"))
            {
                using var m=new MonitorService(Paths.Load());m.Start();int seconds=30;int i=Array.IndexOf(args,"--seconds");if(i>=0&&args.Length>i+1)int.TryParse(args[i+1],out seconds);Thread.Sleep(Math.Clamp(seconds,3,3600)*1000);return 0;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);return Environment.ExitCode;
        }
        catch(Exception e){Paths.Log("fatal",e);File.WriteAllText(Path.Combine(Paths.Root,"startup-error.txt"),e.GetType().Name+": "+e.Message+(args.Contains("--ui-test")?"\n"+e.StackTrace:""));return 1;}
    }
    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
