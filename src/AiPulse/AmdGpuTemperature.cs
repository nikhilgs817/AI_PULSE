using System.Runtime.InteropServices;

namespace AiPulse;

// Read-only subset of AMD's ADLX C ABI. Load the copy supplied by the Windows display driver.
internal sealed class AmdGpuTemperature : IDisposable
{
    const int SystemGetGpus = 1;
    const int SystemGetPerformanceServices = 9;
    const int InterfaceRelease = 1;
    const int GpuListSize = 3;
    const int GpuListAt = 11;
    const int GpuName = 7;
    const int GetCurrentGpuMetrics = 18;
    const int GpuEdgeTemperature = 7;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryVersion(out ulong version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Initialize(ulong version,out IntPtr system);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Terminate();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPointer(IntPtr self,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetGpu(IntPtr self,uint index,out IntPtr gpu);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Size(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseReference(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetGpuMetrics(IntPtr self,IntPtr gpu,out IntPtr metrics);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetTemperature(IntPtr self,out double temperature);

    IntPtr library,performance,adapter;
    Terminate? terminate;
    string? adapterName;

    AmdGpuTemperature(IntPtr library)=>this.library=library;

    internal static AmdGpuTemperature? Open(string device)
    {
        if(!OperatingSystem.IsWindows()||RuntimeInformation.ProcessArchitecture!=Architecture.X64||
            !NativeLibrary.TryLoad("amdadlx64.dll",typeof(AmdGpuTemperature).Assembly,DllImportSearchPath.System32,out var library))
            return null;

        var reader=new AmdGpuTemperature(library);
        try
        {
            var query=reader.Export<QueryVersion>("ADLXQueryFullVersion");
            var initialize=reader.Export<Initialize>("ADLXInitialize");
            var terminate=reader.Export<Terminate>("ADLXTerminate");
            if(query(out var version)==0&&initialize(version,out var system)==0)
            {
                reader.terminate=terminate;
                if(system!=IntPtr.Zero&&reader.FindAdapter(system,device))return reader;
            }
        }
        catch(Exception e) when(e is EntryPointNotFoundException or ArgumentException)
        {
            Paths.Log("amd-adlx",e);
        }
        reader.Dispose();
        return null;
    }

    bool FindAdapter(IntPtr system,string device)
    {
        if(Method<GetPointer>(system,SystemGetGpus)(system,out var gpus)!=0||gpus==IntPtr.Zero)return false;
        try
        {
            uint count=Method<Size>(gpus,GpuListSize)(gpus);
            for(uint index=0;index<count;index++)
            {
                if(Method<GetGpu>(gpus,GpuListAt)(gpus,index,out var gpu)!=0||gpu==IntPtr.Zero)continue;
                try
                {
                    if(Method<GetPointer>(gpu,GpuName)(gpu,out var name)!=0||
                        !string.Equals(Marshal.PtrToStringAnsi(name),device,StringComparison.OrdinalIgnoreCase))continue;
                    if(adapter!=IntPtr.Zero)return false; // Identical names cannot identify a single adapter.
                    adapter=gpu;
                    gpu=IntPtr.Zero;
                }
                finally{Release(gpu);}
            }
            if(adapter==IntPtr.Zero)return false;
            adapterName=device;
            return Method<GetPointer>(system,SystemGetPerformanceServices)(system,out performance)==0&&performance!=IntPtr.Zero;
        }
        finally{Release(gpus);}
    }

    internal double? Read(string device)
    {
        if(!string.Equals(device,adapterName,StringComparison.OrdinalIgnoreCase))return null;
        if(Method<GetGpuMetrics>(performance,GetCurrentGpuMetrics)(performance,adapter,out var sample)!=0||sample==IntPtr.Zero)
            return null;
        try
        {
            return Method<GetTemperature>(sample,GpuEdgeTemperature)(sample,out var value)==0?J.SensorValue(value,"°C"):null;
        }
        finally{Release(sample);}
    }

    T Export<T>(string name) where T:Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));

    static T Method<T>(IntPtr instance,int slot) where T:Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),slot*IntPtr.Size));

    static void Release(IntPtr instance)
    {
        if(instance!=IntPtr.Zero)Method<ReleaseReference>(instance,InterfaceRelease)(instance);
    }

    public void Dispose()
    {
        if(library==IntPtr.Zero)return;
        Release(performance);
        Release(adapter);
        terminate?.Invoke();
        NativeLibrary.Free(library);
        library=IntPtr.Zero;
    }
}
