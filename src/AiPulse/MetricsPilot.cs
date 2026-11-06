using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiPulse;

// A private, optional comparison feed; the existing history remains authoritative.
internal static class MetricsPilot
{
    static readonly Dictionary<string,string> names=new()
    {
        ["cpu.load"]="aipulse_cpu_utilization_percent",["gpu.load"]="aipulse_gpu_utilization_percent",
        ["cpu.temperature"]="aipulse_cpu_temperature_celsius",["gpu.temperature"]="aipulse_gpu_temperature_celsius",
        ["ram.used"]="aipulse_ram_used_bytes",["ram.total"]="aipulse_ram_total_bytes",
        ["gpu.local"]="aipulse_gpu_local_bytes"
    };

    public static async Task Run(MonitorService monitor,int port,CancellationToken ct)
    {
        try
        {
            var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[]});
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,port));
            await using var app=builder.Build();
            app.MapGet("/metrics",(HttpContext context)=>
            {
                if(context.Request.Host.Host!="127.0.0.1"||context.Request.Headers.ContainsKey("Origin"))return Results.StatusCode(403);
                bool fresh=monitor.Fresh;
                var output=new StringBuilder().Append("aipulse_sample_fresh ").Append(fresh?"1\n":"0\n");
                if(fresh)foreach(var reading in monitor.Readings)
                {
                    if(!names.TryGetValue(reading.Id,out var name)||reading.Value is not double value||!double.IsFinite(value))continue;
                    if(name.EndsWith("_bytes"))
                    {
                        if(reading.Unit!="GiB")continue;
                        value*=1073741824;
                    }
                    output.Append(name).Append(' ').Append(value.ToString("R",CultureInfo.InvariantCulture)).Append('\n');
                }
                return Results.Text(output.ToString(),"text/plain; version=0.0.4; charset=utf-8");
            });
            await app.RunAsync(ct);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception e){Paths.Log("metrics-pilot",e);}
    }
}
