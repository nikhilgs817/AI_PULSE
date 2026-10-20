using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AiPulse;

public static class FleetInstaller
{
    public static string Status(string role)
    {
        try
        {
            if(OperatingSystem.IsWindows()){using var service=new System.ServiceProcess.ServiceController(role=="hub"?"AIPULSEHub":"AIPULSEAgent");return service.Status.ToString();}
            var info=new ProcessStartInfo("systemctl"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true,ArgumentList={"is-active","aipulse-"+role}};using var process=Process.Start(info)!;string result=process.StandardOutput.ReadToEnd().Trim();process.WaitForExit(2000);return result;
        }catch{return "Not installed / unavailable";}
    }
    public static void Remove(string role)
    {
        if(role is not ("hub" or "agent"))throw new ArgumentException("Choose hub or agent");
        if(OperatingSystem.IsWindows())
        {
            string name=role=="hub"?"AIPULSEHub":"AIPULSEAgent";using var controller=new System.ServiceProcess.ServiceController(name);if(controller.Status!=System.ServiceProcess.ServiceControllerStatus.Stopped){controller.Stop();controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}Run("sc.exe","delete",name);
            if(role=="hub")Run("netsh.exe","advfirewall","firewall","delete","rule","name=AIPULSE Hub");
        }
        else{Run("systemctl","disable","--now","aipulse-"+role);File.Delete("/etc/systemd/system/aipulse-"+role+".service");Run("systemctl","daemon-reload");}
        // Keep history, pairing identity and installed runtime so removal never deletes monitoring data.
    }
    public static string ServiceRoot(string role)=>OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AIPULSE",role):"/var/lib/aipulse-"+role;
    static void Run(string exe,params string[] arguments)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};foreach(var arg in arguments)info.ArgumentList.Add(arg);
        using var p=Process.Start(info)??throw new IOException("Could not start service manager");string output=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();if(!p.WaitForExit(30000)||p.ExitCode!=0)throw new IOException("Service manager failed: "+Path.GetFileName(exe));
    }
    public static void Install(string role,string sourceConfig)
    {
        if(role is not ("hub" or "agent"))throw new ArgumentException("Choose hub or agent");
        sourceConfig=Path.GetFullPath(sourceConfig);
        if(role=="hub")_ = FleetFiles.Read<HubConfig>(Path.Combine(sourceConfig,"hub.json"));else FleetFiles.ValidateConnection(FleetFiles.Read<HostAgentConfig>(Path.Combine(sourceConfig,"agent.json")).Connection);
        string root=ServiceRoot(role);
        string binaryRoot=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"AIPULSE Services",role):"/usr/lib/aipulse-services/"+role;
        string exe=Path.Combine(binaryRoot,OperatingSystem.IsWindows()?"AiPulse.exe":"AiPulse");
        if(OperatingSystem.IsWindows())
        {
            if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Service installation requires Windows administrator approval");
            string service=role=="hub"?"AIPULSEHub":"AIPULSEAgent";
            try{using var controller=new System.ServiceProcess.ServiceController(service);if(controller.Status!=System.ServiceProcess.ServiceControllerStatus.Stopped){controller.Stop();controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}}catch(InvalidOperationException){}
            foreach(var folder in new[]{Path.GetDirectoryName(binaryRoot)!,binaryRoot,Path.GetDirectoryName(root)!,root})
            {
                ValidateServiceDirectory(folder);
                Directory.CreateDirectory(folder);ProtectDirectory(folder);
            }
            CopyRuntime(AppContext.BaseDirectory,binaryRoot);
            CopyConfig(role,sourceConfig,root);
            string command="\""+exe+"\" --"+role+" --data-dir \""+root+"\"";
            try{Run("sc.exe","create",service,"binPath=",command,"start=","auto","DisplayName=",role=="hub"?"AIPULSE Hub":"AIPULSE Host Agent");}
            catch(IOException){Run("sc.exe","config",service,"binPath=",command,"start=","auto");}
            Run("sc.exe","failure",service,"reset=","86400","actions=","restart/5000/restart/15000/restart/60000");Run("sc.exe","start",service);
            if(role=="hub"){try{Run("netsh.exe","advfirewall","firewall","delete","rule","name=AIPULSE Hub");}catch(IOException){}var hub=FleetFiles.Read<HubConfig>(Path.Combine(root,"hub.json"));Run("netsh.exe","advfirewall","firewall","add","rule","name=AIPULSE Hub","dir=in","action=allow","protocol=TCP","localport="+hub.Port,"program="+exe,"profile=private,domain");}
        }
        else
        {
            if(Environment.UserName!="root")throw new UnauthorizedAccessException("Service installation requires administrator privileges");
            Directory.CreateDirectory(root);Directory.CreateDirectory(binaryRoot);File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            CopyRuntime(AppContext.BaseDirectory,binaryRoot);CopyConfig(role,sourceConfig,root);
            try{Run("id","aipulse");}catch(IOException){Run("useradd","--system","--no-create-home","--shell","/usr/sbin/nologin","aipulse");}
            string account="aipulse";
            if(role=="agent"&&FleetFiles.Read<HostAgentConfig>(Path.Combine(root,"agent.json")).UsageRoots.Count>0)
            {
                string? origin=Environment.GetEnvironmentVariable("SUDO_UID")??Environment.GetEnvironmentVariable("PKEXEC_UID");
                if(int.TryParse(origin,out int uid)&&uid>0&&File.ReadLines("/etc/passwd").Any(line=>line.Split(':') is var parts&&parts.Length>3&&parts[2]==uid.ToString()))account=uid.ToString();
            }
            Run("chown","-R",account,root);Run("chmod","-R","go-rwx",root);Run("chmod","+x",exe);
            string unit="aipulse-"+role;
            File.WriteAllText("/etc/systemd/system/"+unit+".service",$"[Unit]\nDescription=AIPULSE {role}\nAfter=network.target\n\n[Service]\nType=simple\nUser={account}\nWorkingDirectory={root}\nEnvironment=DOTNET_EnableDiagnostics=0\nPrivateTmp=true\nExecStart={exe} --{role} --data-dir {root}\nRestart=always\nRestartSec=5\nUMask=0077\nNoNewPrivileges=true\nProtectSystem=strict\nProtectHome=read-only\nReadWritePaths={root}\n\n[Install]\nWantedBy=multi-user.target\n");
            Run("systemctl","daemon-reload");Run("systemctl","enable","--now",unit);
        }
    }
    static void CopyConfig(string role,string source,string destination)
    {
        if(role=="agent"&&File.Exists(Path.Combine(source,"history.db"))&&!File.Exists(Path.Combine(destination,"data","pulse.db")))
        {
            Storage.VerifyBackup(Path.Combine(source,"history.db"));Directory.CreateDirectory(Path.Combine(destination,"data"));
            File.Copy(Path.Combine(source,"history.db"),Path.Combine(destination,"data","pulse.db"));
            File.WriteAllText(Path.Combine(destination,"seed-history"),"Seed retained minute history and usage on first agent start.");
        }
        // Identity and credentials survive upgrades. Config replacement is an explicit separate operation.
        foreach(string name in role=="hub"?new[]{"hub.json","hub.pfx"}:new[]{"agent.json","settings.json"})
        {
            string target=Path.Combine(destination,name);if(File.Exists(target))continue;
            string file=Path.Combine(source,name);if(File.Exists(file)){File.Copy(file,target);FleetFiles.Protect(target);}
        }
    }
    static void CopyRuntime(string source,string destination)
    {
        foreach(var file in Directory.EnumerateFiles(source))
        {
            string name=Path.GetFileName(file);
            if(Path.GetExtension(name) is not (".dll" or ".so" or ".dylib" or ".json" or ".exe")&&name!="AiPulse")continue;
            if(name.EndsWith(".json")&&!name.EndsWith(".deps.json")&&!name.EndsWith(".runtimeconfig.json"))continue;
            if(name.EndsWith(".exe")&&name!="AiPulse.exe")continue;
            File.Copy(file,Path.Combine(destination,name),true);
        }
        string runtimes=Path.Combine(source,"runtimes");if(Directory.Exists(runtimes))foreach(var file in Directory.EnumerateFiles(runtimes,"*",SearchOption.AllDirectories)){string target=Path.Combine(destination,"runtimes",Path.GetRelativePath(runtimes,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);}
    }
    static void ValidateServiceDirectory(string path)
    {
        if(!OperatingSystem.IsWindows())return;
        if(!Directory.Exists(path))return;
        if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("Service directories cannot be reparse points");
        var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var acl=new DirectoryInfo(path).GetAccessControl();var owner=acl.GetOwner(typeof(SecurityIdentifier));
        if(!admin.Equals(owner)&&!system.Equals(owner))throw new UnauthorizedAccessException("Existing service directory has an untrusted owner");
        var write=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&write)!=0&&!admin.Equals(rule.IdentityReference)&&!system.Equals(rule.IdentityReference))throw new UnauthorizedAccessException("Existing service directory permits unprivileged writes");
    }
    static void ProtectDirectory(string directory)
    {
        if(!OperatingSystem.IsWindows())return;
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
}
