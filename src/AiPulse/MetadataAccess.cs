using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AiPulse;

public sealed class MetadataUserUnavailableException:Exception
{
    public MetadataUserUnavailableException():base("Waiting for the configured Windows user to sign in; machine collection continues."){}
}

// Only directory enumeration and file reads run in this context. Database writes retain
// the service identity. No password is stored, and another user's token is never used.
public sealed class MetadataAccess(string expectedSid)
{
    public T Read<T>(Func<T> work)
    {
        if(!OperatingSystem.IsWindows())return work();
        if(string.IsNullOrWhiteSpace(expectedSid))throw new MetadataUserUnavailableException();
        uint session=WTSGetActiveConsoleSessionId();
        if(session==uint.MaxValue||!WTSQueryUserToken(session,out var token))throw new MetadataUserUnavailableException();
        using(token)
        {
            using var identity=new WindowsIdentity(token.DangerousGetHandle());
            if(identity.User?.Value!=expectedSid)throw new MetadataUserUnavailableException();
            return WindowsIdentity.RunImpersonated(token,work);
        }
    }
    [DllImport("kernel32.dll")]static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool WTSQueryUserToken(uint sessionId,out SafeAccessTokenHandle token);
}
