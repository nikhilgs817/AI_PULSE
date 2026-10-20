using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AiPulse;

public sealed class FleetConnection
{
    public string Url {get;set;}="https://localhost:11440";
    public string NetworkType {get;set;}="Private LAN";
    public string CertificateSha256 {get;set;}="";
    public string Token {get;set;}="";
}
public sealed class HubConfig
{
    public string HubId {get;set;}=Guid.NewGuid().ToString("N");
    public string ListenAddress {get;set;}="0.0.0.0";
    public int Port {get;set;}=11440;
    public string CertificateFile {get;set;}="hub.pfx";
    public string AdminHash {get;set;}="";
    public int RetentionDays {get;set;}=90;
    public string EnrollmentSecret {get;set;}=FleetFiles.Secret();
}
public sealed class HostAgentConfig
{
    public string InstallationId {get;set;}=Guid.NewGuid().ToString("N");
    public string HostId {get;set;}="";
    public string Name {get;set;}=Environment.MachineName;
    public string PairingCode {get;set;}="";
    public string HubEpoch {get;set;}="";
    public FleetConnection Connection {get;set;}=new();
    public string WindowsUserSid {get;set;}="";
    public List<string> UsageRoots {get;set;}=[];
}
public record Enrollment(string Code,string InstallationId,string Name,string NetworkType);
public record EnrollmentResult(string HostId,string Token,string HubId);
public record PairingTicket(string Code,long ExpiresAt);
public record IngestBatch(string InstallationId,List<SyncEvent> Events);
public record IngestAck(string HubId,List<long> Accepted);
public record HostSummary(string Id,string Name,string Platform,string NetworkType,long ReceivedAt,long SampledAt,string Status,long QueueCount,long QueueBytes);
public sealed record HostSnapshot
{
    public string Name {get;init;}=Environment.MachineName;
    public string Platform {get;init;}=OperatingSystem.IsWindows()?"Windows":"Ubuntu / Linux";
    public string Cpu {get;init;}="CPU";
    public string Gpu {get;init;}="GPU";
    public int LogicalProcessors {get;init;}=Environment.ProcessorCount;
    public long SampledAt {get;init;}
    public string Health {get;init;}="Starting";
    public string AgentStatus {get;init;}="User token coverage unavailable";
    public string GatewayStatus {get;init;}="Not enabled";
    public string? LastBackup {get;init;}
    public string? BackupError {get;init;}
    public bool ReadAgentUsage {get;init;}
    public int RetentionDays {get;init;}=90;
    public List<Reading> Readings {get;init;}=[];
    public List<HardwareItem> Inventory {get;init;}=[];
    public List<ProcessInfo> Processes {get;init;}=[];
    public List<AgentResource> AgentApps {get;init;}=[];
    public DateTime ProcessSampleTime {get;init;}
    public int UnreadableProcesses {get;init;}
    public List<ModelInfo> Models {get;init;}=[];
    public List<ModelInfo> Installed {get;init;}=[];
    public Dictionary<string,int> ActiveRequests {get;init;}=[];
    public Dictionary<string,string> ModelStatus {get;init;}=[];
    public long QueueCount {get;init;}
    public long QueueBytes {get;init;}
}
public record HostView(string HubId,HostSummary Host,HostSnapshot? Snapshot,List<SyncEvent> Events,long FirstSequence=0,long LatestSequence=0);

public static class FleetFiles
{
    public static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true,WriteIndented=true};
    public static string Secret()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static bool Matches(string secret,string hash)=>CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(J.Hash(secret)),System.Text.Encoding.UTF8.GetBytes(hash));
    public static T Read<T>(string file)=>JsonSerializer.Deserialize<T>(File.ReadAllText(file),Json)??throw new InvalidDataException("Invalid fleet configuration");
    public static void Write<T>(string file,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temp=file+".tmp";using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {Protect(temp);JsonSerializer.Serialize(f,value,Json);f.Flush(true);}File.Move(temp,file,true);
    }
    public static void Protect(string file)
    {
        if(OperatingSystem.IsWindows())
        {
            var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
            var administrator=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            bool elevated=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if(elevated)acl.SetOwner(administrator);else acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,AccessControlType.Allow));
            foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),administrator})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    public static Uri ValidateConnection(FleetConnection connection)
    {
        if(connection.NetworkType is not ("Private LAN" or "VPN" or "Internet"))throw new InvalidDataException("Choose Private LAN, VPN or Internet");
        if(!Uri.TryCreate(connection.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||uri.AbsolutePath!="/")throw new InvalidDataException("Use an HTTPS hub address without a path, credentials or query");
        if(connection.CertificateSha256.Length>0&&(connection.CertificateSha256.Length!=64||!connection.CertificateSha256.All(Uri.IsHexDigit)))throw new InvalidDataException("Certificate fingerprint must be 64 hexadecimal characters");
        if(connection.NetworkType!="Internet"&&connection.CertificateSha256.Length==0)throw new InvalidDataException("Private LAN and VPN require the hub's verified certificate fingerprint");
        return uri;
    }
    public static HttpClient Client(FleetConnection connection)
    {
        var uri=ValidateConnection(connection);
        var handler=new HttpClientHandler{AllowAutoRedirect=false};
        handler.ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>
        {
            if(cert==null||DateTime.UtcNow<cert.NotBefore.ToUniversalTime()||DateTime.UtcNow>cert.NotAfter.ToUniversalTime())return false;
            if(connection.NetworkType=="Internet"&&errors!=SslPolicyErrors.None)return false;
            return connection.CertificateSha256.Length==0?errors==SslPolicyErrors.None:string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256),connection.CertificateSha256,StringComparison.OrdinalIgnoreCase);
        };
        var client=new HttpClient(handler){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=4*1024*1024};
        if(connection.Token.Length>0)client.DefaultRequestHeaders.Authorization=new("Bearer",connection.Token);
        return client;
    }
    public static FleetConnection InitializeHub(string root,string hostName="localhost")
    {
        Directory.CreateDirectory(root);string configFile=Path.Combine(root,"hub.json"),connectionFile=Path.Combine(root,"dashboard-connection.json");
        if(File.Exists(configFile))return Read<FleetConnection>(connectionFile);
        if(Uri.CheckHostName(hostName)==UriHostNameType.Unknown)throw new ArgumentException("Use the hub computer's DNS name or IP address");
        using var key=RSA.Create(3072);var request=new CertificateRequest("CN=AIPULSE Hub",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);if(IPAddress.TryParse(hostName,out var ip))san.AddIpAddress(ip);else if(hostName!="localhost")san.AddDnsName(hostName);
        request.CertificateExtensions.Add(san.Build());request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,false));
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));
        string pfx=Path.Combine(root,"hub.pfx");using(var file=new FileStream(pfx,FileMode.CreateNew,FileAccess.Write,FileShare.None)){Protect(pfx);file.Write(cert.Export(X509ContentType.Pfx));file.Flush(true);}
        string token=Secret();var config=new HubConfig{AdminHash=J.Hash(token)};Write(configFile,config);
        var connection=new FleetConnection{Url="https://"+(ip?.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?"["+hostName+"]":hostName)+":11440",CertificateSha256=cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),Token=token};Write(connectionFile,connection);return connection;
    }
}
