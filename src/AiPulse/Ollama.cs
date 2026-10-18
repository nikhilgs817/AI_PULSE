using System.Text.Json;
using System.Collections.Concurrent;

namespace AiPulse;

public sealed class Ollama : IDisposable
{
    readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(4)};
    public List<ModelInfo> Models {get;private set;}=[];
    public List<ModelInfo> Installed {get;private set;}=[];
    public Dictionary<string,string> Status {get;private set;}=[];
    readonly Dictionary<string,List<ModelInfo>> inventory=[];
    DateTime nextInventory=DateTime.MinValue;
    public ConcurrentDictionary<string,int> Active {get;}=new();
    public async Task Poll(AppSettings settings,CancellationToken ct)
    {
        var endpoints=new List<Endpoint>{new("This PC",settings.OllamaUrl)};endpoints.AddRange(settings.RemoteEndpoints);
        var models=new List<ModelInfo>();var status=new Dictionary<string,string>();bool refresh=DateTime.UtcNow>nextInventory;
        foreach(var endpoint in endpoints)
        {
            try
            {
                var url=Validate(endpoint.Url);using var response=await client.GetAsync(url+"/api/ps",ct);response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach(var item in doc.RootElement.Get("models").EnumerateArray())
                {var name=item.Str("name");var details=item.Get("details");bool active=endpoint.Name=="This PC"&&(Active.GetValueOrDefault(name)>0||name.EndsWith(":latest")&&Active.GetValueOrDefault(name[..^7])>0);models.Add(new(endpoint.Name,name,active?"Generating":"Loaded",item.Dbl("size")/1073741824,item.Dbl("size_vram")/1073741824,details.Str("quantization_level"),item.Num("context_length"),item.Str("digest")));}
                status[endpoint.Name]="Connected";
                if(refresh||!inventory.ContainsKey(endpoint.Name))
                {using var tags=await client.GetAsync(url+"/api/tags",ct);tags.EnsureSuccessStatusCode();using var td=JsonDocument.Parse(await tags.Content.ReadAsStringAsync(ct));inventory[endpoint.Name]=td.RootElement.Get("models").EnumerateArray().Select(m=>new ModelInfo(endpoint.Name,m.Str("name"),"Installed",m.Dbl("size")/1073741824,null,m.Get("details").Str("quantization_level"),m.Get("details").Num("context_length"),m.Str("digest"))).ToList();}
            }
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException){status[endpoint.Name]="Offline / unavailable";}
        }
        if(refresh)nextInventory=DateTime.UtcNow.AddMinutes(1);
        Models=models;Installed=endpoints.Where(e=>inventory.ContainsKey(e.Name)).SelectMany(e=>inventory[e.Name]).ToList();Status=status;
    }
    public static string Validate(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")throw new InvalidOperationException("Use an HTTP(S) endpoint without credentials, query or fragment.");
        return url.TrimEnd('/');
    }
    public void Dispose()=>client.Dispose();
}
