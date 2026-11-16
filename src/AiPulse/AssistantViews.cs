using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia;
using Avalonia.Input;
using System.Diagnostics;

namespace AiPulse;

public sealed partial class MainWindow
{
    readonly LocalAssistant assistant=new();
    CancellationTokenSource? assistantRequest;
    readonly List<(string Question,AssistantReply Reply)> assistantTurns=[];
    string assistantQuestion="",assistantNotice="";
    bool assistantBusy;
    Window? chatWindow;
    ScrollViewer? chatScroll;
    string AssistantScope=>fleet?.SelectedHost??"local";
    void ResetAssistant()
    {
        assistantRequest?.Cancel();assistantTurns.Clear();assistantQuestion="";assistantNotice="";
        RefreshChat();
    }
    Control AssistantSettingsPanel()
    {
        var options=localService.Settings.Assistant;
        if(!LocalAssistant.Profiles.Any(p=>p.Tag==options.Model))options.Model=LocalAssistant.Profiles[0].Tag;
        var panel=new StackPanel{Spacing=16};
        panel.Children.Add(Text("AI",19,null,true));
        panel.Children.Add(Text("Optional local analysis using Ollama. Choose a Google or Microsoft model to download. Chat stays in memory. Model usage counts are recorded.",12,"#8ba0bd"));
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
            finally{assistantBusy=false;if(page=="Settings")RenderBody();RefreshChat();}
        };
        return panel;
    }

    void OpenChat()
    {
        if(chatWindow!=null){chatWindow.Activate();return;}
        chatScroll=new ScrollViewer();
        var window=new Window{Title="Ask Pulse",Width=480,Height=600,MinWidth=330,MinHeight=350,
            Background=Canvas,Foreground=Ink(null),RequestedThemeVariant=RequestedThemeVariant,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=chatScroll};
        chatWindow=window;
        window.Closed+=(_,_)=>{chatWindow=null;chatScroll=null;};
        RefreshChat();
        window.Show(this);
    }

    void RefreshChat()
    {
        if(chatScroll==null)return;
        chatScroll.Content=ChatPanel();
        var viewer=chatScroll;
        Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(chatScroll==viewer)viewer.ScrollToEnd();},Avalonia.Threading.DispatcherPriority.Loaded);
    }

    Control ChatPanel()
    {
        var options=localService.Settings.Assistant;
        var panel=new StackPanel{Spacing=10,Margin=new Thickness(16)};
        panel.Children.Add(Text("Ask Pulse",22,null,true));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        var cancel=Btn("Cancel");cancel.IsEnabled=assistantBusy;cancel.Click+=(_,_)=>assistantRequest?.Cancel();
        bool hostReady=fleet==null||fleet.SelectedHost!=""&&fleet.SelectedMonitor?.RemoteSnapshot!=null;
        if(!hostReady)panel.Children.Add(Message("Select an individual host","Choose a host from the dashboard before asking about its readings."));
        if(!options.Enabled)panel.Children.Add(Text("Model analysis is off. Enable it in Settings → AI. Metric widget requests still work.",12,"#8ba0bd"));
        foreach(var turn in assistantTurns)
        {
            var response=new StackPanel{Spacing=8,Children={Text(turn.Question,14,null,true)}};
            if(turn.Reply.Facts!="")response.Children.Add(Text(turn.Reply.Facts,12,"#8ba0bd"));
            response.Children.Add(Text(turn.Reply.Explanation));
            panel.Children.Add(Card(response));
        }
        var question=new TextBox{Text=assistantQuestion,PlaceholderText="Why is RAM high? Or: Build a 2×2 widget for GPU temperature",MaxLength=1000,AcceptsReturn=true,MinHeight=64,IsEnabled=!assistantBusy};
        question.TextChanged+=(_,_)=>assistantQuestion=question.Text??"";
        Avalonia.Automation.AutomationProperties.SetName(question,"Assistant question");panel.Children.Add(question);
        var send=Btn("Send");send.IsEnabled=hostReady&&!assistantBusy;
        send.Click+=async(_,_)=>{
            if(assistantBusy)return;
            string scope=AssistantScope,input=string.IsNullOrWhiteSpace(assistantQuestion)?"Summarize current resource use.":assistantQuestion;
            if(TryCreateWidget(input,out string result))
            {
                AddChatTurn(input,new("",result,0,0,""));
                assistantQuestion="";RefreshChat();
                if(page=="Overview")RenderBody();
                return;
            }
            var facts=LocalAssistant.Capture(service);assistantRequest?.Dispose();assistantRequest=new();assistantBusy=true;send.IsEnabled=false;question.IsEnabled=false;cancel.IsEnabled=true;long started=J.Now;
            try{
                var reply=await assistant.Ask(options,facts,input,assistantRequest.Token,assistantTurns.LastOrDefault().Question);
                if(AssistantScope!=scope)return;
                AddChatTurn(input,reply);
                if(reply.Model!="")localService.Store.PutUsage(new("assistant:"+Guid.NewGuid().ToString("N"),"AIPULSE Assistant",reply.Model,"dashboard-assistant",started,reply.Input,reply.Output,0,0,0,null,null,"Completed"));
                assistantQuestion="";assistantNotice=$"Last answer: {reply.Input:N0} input / {reply.Output:N0} output tokens. Usage belongs to this dashboard computer.";
            }
            catch(OperationCanceledException){assistantNotice="Question cancelled.";}
            catch(Exception e){Paths.Log("assistant-question",e);assistantNotice=e is ArgumentException or InvalidOperationException?e.Message:"Could not get a local answer. Check Ollama and the selected model. Monitoring continues.";}
            finally{assistantBusy=false;RefreshChat();if(page=="Settings")RenderBody();}
        };
        question.KeyDown+=(_,e)=>{if(e.Key==Key.Enter&&e.KeyModifiers==KeyModifiers.Control&&send.IsEnabled){send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));e.Handled=true;}};
        actions.Children.Add(send);actions.Children.Add(cancel);panel.Children.Add(actions);
        panel.Children.Add(Text(assistantNotice,11,"#8ba0bd"));
        return panel;
    }
    void AddChatTurn(string question,AssistantReply reply)
    {
        assistantTurns.Add((question,reply));
        if(assistantTurns.Count>6)assistantTurns.RemoveAt(0);
    }
    void OpenAssistantSetup()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"docs","ASSISTANT.md");
        if(File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
        else Process.Start(new ProcessStartInfo("https://ollama.com/download"){UseShellExecute=true});
    }
}
