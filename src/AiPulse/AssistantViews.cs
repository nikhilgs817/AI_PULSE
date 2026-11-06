using Avalonia.Controls;
using Avalonia.Layout;
using System.Diagnostics;

namespace AiPulse;

public sealed partial class MainWindow
{
    readonly LocalAssistant assistant=new();
    CancellationTokenSource? assistantRequest;
    readonly List<(string Question,AssistantReply Reply)> assistantTurns=[];
    string assistantQuestion="",assistantNotice="";
    bool assistantBusy;
    string AssistantScope=>fleet?.SelectedHost??"local";
    void ResetAssistant()
    {
        assistantRequest?.Cancel();assistantTurns.Clear();assistantQuestion="";assistantNotice="";
    }
    Control AssistantPage()
    {
        var options=localService.Settings.Assistant;
        if(!LocalAssistant.Profiles.Any(p=>p.Tag==options.Model))options.Model=LocalAssistant.Profiles[0].Tag;
        var panel=new StackPanel{Spacing=16};
        panel.Children.Add(Heading("Assistant · experimental","Local, read-only explanations · measured facts stay separate from AI interpretation"));
        panel.Children.Add(Message("Optional AI pack","No model is bundled. Enable the assistant, choose a Google or Microsoft model, then download it once. Ollama must be installed locally. Chat text stays in memory and is not saved by AIPULSE; only usage counts are retained. The local runtime has its own settings. CPU only, two threads, bounded context; the model unloads after each answer."));
        var setup=new StackPanel{Spacing=12};
        var enabled=new CheckBox{Content="Enable local AI assistant",IsChecked=options.Enabled};
        enabled.IsCheckedChanged+=(_,_)=>{options.Enabled=enabled.IsChecked==true;if(!options.Enabled)assistantRequest?.Cancel();Paths.Save(localService.Settings);RenderBody();};setup.Children.Add(enabled);
        var models=new ComboBox{ItemsSource=LocalAssistant.Profiles.Select(p=>$"{p.Developer} · {p.Label} · {p.ApproximateDownload} download").ToList(),SelectedIndex=Array.FindIndex(LocalAssistant.Profiles,p=>p.Tag==options.Model),HorizontalAlignment=HorizontalAlignment.Stretch,IsEnabled=!assistantBusy};
        models.SelectionChanged+=(_,_)=>{if(models.SelectedIndex>=0){options.Model=LocalAssistant.Profiles[models.SelectedIndex].Tag;Paths.Save(localService.Settings);}};setup.Children.Add(models);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
        var pull=Btn("Download selected model");pull.IsEnabled=options.Enabled&&!assistantBusy;actions.Children.Add(pull);
        var instructions=Btn("Runtime and setup instructions");instructions.Click+=(_,_)=>OpenAssistantSetup();actions.Children.Add(instructions);
        var cancel=Btn("Cancel");cancel.IsEnabled=assistantBusy;cancel.Click+=(_,_)=>assistantRequest?.Cancel();actions.Children.Add(cancel);setup.Children.Add(actions);
        var status=Text(assistantNotice,12,"#91cfe3");setup.Children.Add(status);panel.Children.Add(Card(setup));
        pull.Click+=async(_,_)=>{
            assistantRequest?.Dispose();assistantRequest=new();assistantBusy=true;pull.IsEnabled=false;models.IsEnabled=false;cancel.IsEnabled=true;
            try{await assistant.Pull(options,new Progress<string>(message=>{assistantNotice=message;status.Text=message;}),assistantRequest.Token);assistantNotice="Model downloaded. It will load only when you ask a question.";}
            catch(OperationCanceledException){assistantNotice="Download cancelled; Ollama can resume it later.";}
            catch(Exception e){Paths.Log("assistant-model-download",e);assistantNotice="Download unavailable. Check that local Ollama is running, then retry. Nothing was enabled remotely.";}
            finally{assistantBusy=false;if(page=="Assistant")RenderBody();}
        };
        bool hostReady=fleet==null||fleet.SelectedHost!=""&&fleet.SelectedMonitor?.RemoteSnapshot!=null;
        if(!hostReady)panel.Children.Add(Message("Select an individual host","Use the host dropdown before asking about monitoring data. Assistant setup belongs to this dashboard computer."));
        var question=new TextBox{Text=assistantQuestion,PlaceholderText="Ask about this host, e.g. summarize resource use or explain missing temperatures",MaxLength=1000,AcceptsReturn=true,MinHeight=80,IsEnabled=!assistantBusy};
        question.TextChanged+=(_,_)=>assistantQuestion=question.Text??"";
        Avalonia.Automation.AutomationProperties.SetName(question,"Assistant question");panel.Children.Add(question);
        var send=Btn(options.Enabled?"Ask local assistant":"Show measured summary (AI off)");send.IsEnabled=hostReady&&!assistantBusy;
        send.Click+=async(_,_)=>{
            if(assistantBusy)return;
            string scope=AssistantScope,input=string.IsNullOrWhiteSpace(assistantQuestion)?"Summarize current resource use.":assistantQuestion;
            var facts=LocalAssistant.Capture(service);assistantRequest?.Dispose();assistantRequest=new();assistantBusy=true;send.IsEnabled=false;question.IsEnabled=false;cancel.IsEnabled=true;long started=J.Now;
            try{
                var reply=await assistant.Ask(options,facts,input,assistantRequest.Token);
                if(AssistantScope!=scope)return;
                assistantTurns.Add((input,reply));while(assistantTurns.Count>6)assistantTurns.RemoveAt(0);
                if(reply.Model!="")localService.Store.PutUsage(new("assistant:"+Guid.NewGuid().ToString("N"),"AIPULSE Assistant",reply.Model,"dashboard-assistant",started,reply.Input,reply.Output,0,0,0,null,null,"Completed"));
                assistantQuestion="";assistantNotice=$"Last answer: {reply.Input:N0} input / {reply.Output:N0} output tokens. Usage belongs to this dashboard computer.";
            }
            catch(OperationCanceledException){assistantNotice="Question cancelled.";}
            catch(Exception e){Paths.Log("assistant-question",e);assistantNotice=e is ArgumentException or InvalidOperationException?e.Message:"Could not get a local answer. Check Ollama and the selected model. Monitoring continues.";}
            finally{assistantBusy=false;if(page=="Assistant")RenderBody();}
        };panel.Children.Add(send);
        foreach(var turn in assistantTurns.AsEnumerable().Reverse()){
            var response=new StackPanel{Spacing=10};response.Children.Add(Text(turn.Question,16,null,true));response.Children.Add(Text("Measured facts",13,"#75d5e6",true));response.Children.Add(Text(turn.Reply.Facts));response.Children.Add(Text("AI interpretation · may be inaccurate",12,"#bba3ff",true));response.Children.Add(Text(turn.Reply.Explanation));panel.Children.Add(Card(response));
        }
        return panel;
    }
    void OpenAssistantSetup()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"docs","ASSISTANT.md");
        if(File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
        else Process.Start(new ProcessStartInfo("https://ollama.com/download"){UseShellExecute=true});
    }
}
