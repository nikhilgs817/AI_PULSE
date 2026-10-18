using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

namespace AiPulse;

public interface IWindowsSensors : IDisposable
{
    string CpuName {get;}
    string GpuName {get;}
    List<Reading> Sample();
}

public static class WindowsSensorFactory
{
    // Keep the Windows-only NuGet runtime out of the shared collector's JIT path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IWindowsSensors Create()=>new WindowsSensors();
}

sealed class WindowsSensors : IWindowsSensors
{
    readonly Computer computer=new(){IsCpuEnabled=true,IsGpuEnabled=true,IsMemoryEnabled=true,IsStorageEnabled=true,IsMotherboardEnabled=true,IsNetworkEnabled=false};
    public string CpuName{get;private set;}="CPU";
    public string GpuName{get;private set;}="GPU";
    public WindowsSensors()=>computer.Open();
    public List<Reading> Sample(){var rows=new List<Reading>();foreach(var h in computer.Hardware)Visit(h,rows);return rows;}
    void Visit(IHardware h,List<Reading> rows)
    {
        h.Update();if(h.HardwareType==HardwareType.Cpu)CpuName=h.Name;
        if(h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)GpuName=h.Name;
        foreach(var s in h.Sensors)
        {
            string unit=s.SensorType switch{SensorType.Temperature=>"°C",SensorType.Load=>"%",SensorType.Clock=>"MHz",SensorType.Fan=>"RPM",SensorType.Power=>"W",SensorType.Voltage=>"V",SensorType.Data=>"GiB",SensorType.SmallData=>"MiB",SensorType.Throughput=>"B/s",_=>s.SensorType.ToString()};
            double? value=s.Value is float f&&float.IsFinite(f)?f:null;
            if(unit=="°C"&&value is <=0 or >150)value=null;
            rows.Add(new("sensor"+s.Identifier,s.Name,h.Name,unit,value,"LibreHardwareMonitor"));
        }
        foreach(var sub in h.SubHardware)Visit(sub,rows);
    }
    public void Dispose()=>computer.Close();
}
