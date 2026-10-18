using System.Diagnostics;
using Microsoft.Win32;

namespace AiPulse;
public static class Startup
{
    public static void Configure(bool enabled)
    {
        string executable=Environment.ProcessPath??throw new InvalidOperationException("Executable path unavailable");
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Startup requires the packaged application.");
        if(OperatingSystem.IsWindows())
        {
            using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(enabled)key.SetValue("AI Pulse",$"\"{executable}\" --data-dir \"{Paths.Root}\"");else key.DeleteValue("AI Pulse",false);
        }
        else
        {
            string dir=Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".config"),"autostart");Directory.CreateDirectory(dir);string file=Path.Combine(dir,"ai-pulse.desktop");
            static string Quote(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("`","\\`").Replace("$","\\$")+"\"";
            if(enabled)File.WriteAllText(file,$"[Desktop Entry]\nType=Application\nName=AI Pulse\nExec={Quote(executable)} --data-dir {Quote(Paths.Root)}\nTerminal=false\n");else if(File.Exists(file))File.Delete(file);
        }
    }
}
