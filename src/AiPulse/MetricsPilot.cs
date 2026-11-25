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

    /// <summary>Writes a lab's report when a script stops it; set by the window so reports match the ones made from the app.</summary>
    public static Func<LabSessions.Lab,string>? LabReport { get; set; }

    // Loopback only, and no browser pages: a page on another site cannot drive the lab API or read metrics.
    static bool Local(HttpContext context)=>context.Request.Host.Host=="127.0.0.1"&&!context.Request.Headers.ContainsKey("Origin");

    static string Label(string value)=>value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n"," ");

    /// <summary>The Prometheus text exposition: hardware gauges, today's tokens, reported cost, alerts, models and lab state.</summary>
    internal static string Render(MonitorService monitor)
    {
        var output=new StringBuilder();
        void Line(string name,double value,string labels=""){if(double.IsFinite(value))output.Append(name).Append(labels==""?"":"{"+labels+"}").Append(' ').Append(value.ToString("R",CultureInfo.InvariantCulture)).Append('\n');}
        bool fresh=monitor.Fresh;
        Line("aipulse_sample_fresh",fresh?1:0);
        if(fresh)foreach(var reading in monitor.Readings)
        {
            if(!names.TryGetValue(reading.Id,out var name)||reading.Value is not double value||!double.IsFinite(value))continue;
            if(name.EndsWith("_bytes"))
            {
                if(reading.Unit!="GiB")continue;
                value*=1073741824;
            }
            Line(name,value);
        }
        long today=Budgets.StartOfLocalDay(DateTimeOffset.Now);
        try
        {
            output.Append("# HELP aipulse_tokens_today Tokens observed since local midnight.\n# TYPE aipulse_tokens_today gauge\n");
            foreach(var row in monitor.Store.TokenBreakdown(today))
            {
                string labels=$"source=\"{Label(row.Source)}\",model=\"{Label(row.Model)}\"";
                Line("aipulse_tokens_today",row.Input,labels+",kind=\"input\"");
                Line("aipulse_tokens_today",row.Output,labels+",kind=\"output\"");
                Line("aipulse_tokens_today",row.Cached,labels+",kind=\"cache_read\"");
            }
            Line("aipulse_claude_code_cost_today_usd",monitor.Store.ClaudeCostSince(today));
            Line("aipulse_alerts_active",monitor.Store.Alerts().Count(a=>a.Active));
        }
        catch(Microsoft.Data.Sqlite.SqliteException){}
        Line("aipulse_models_loaded",monitor.Ollama.Models.Count);
        foreach(var gpu in monitor.GpuProcesses.Where(g=>g.MemoryMiB!=null))Line("aipulse_gpu_process_memory_bytes",gpu.MemoryMiB!.Value*1048576.0,$"process=\"{Label(gpu.Name)}\",pid=\"{gpu.Pid}\"");
        if(monitor.GpuThrottle is {} throttle)Line("aipulse_gpu_throttled",throttle.Active?1:0,$"reason=\"{Label(throttle.Reasons)}\"");
        Line("aipulse_lab_running",LabSessions.Shared.Current!=null?1:0);
        return output.ToString();
    }

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
                if(!Local(context))return Results.StatusCode(403);
                return Results.Text(Render(monitor),"text/plain; version=0.0.4; charset=utf-8");
            });
            // Lab runs for scripts: start before an experiment, stop after it, and the run shows up on Token for before/after comparison.
            app.MapPost("/labs/start",(HttpContext context)=>
            {
                if(!Local(context))return Results.StatusCode(403);
                string name=context.Request.Query["name"].ToString();
                if(name.Length>120||name.Any(char.IsControl))return Results.BadRequest("Use a lab name of up to 120 characters.");
                var lab=LabSessions.Shared.Start(name,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return Results.Json(new{lab.Id,lab.Name,lab.StartMs});
            });
            app.MapPost("/labs/stop",async(HttpContext context)=>
            {
                if(!Local(context))return Results.StatusCode(403);
                var lab=await Task.Run(()=>LabSessions.Shared.Stop(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),l=>LabReport?.Invoke(l)??""));
                return lab==null?Results.NotFound("No lab is running."):Results.Json(new{lab.Id,lab.Name,lab.StartMs,lab.EndMs,lab.Report});
            });
            app.MapGet("/labs",(HttpContext context)=>!Local(context)?Results.StatusCode(403):Results.Json(LabSessions.Shared.All().TakeLast(50)));
            await app.RunAsync(ct);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception e){Paths.Log("metrics-pilot",e);}
    }
}
