using System.IO.Pipes;
using Avalonia.Threading;

namespace AiPulse;

// A second shortcut launch only asks the existing UI to become visible.
// No file paths, commands, or settings are accepted over this user-restricted pipe.
public sealed class InstanceActivation : IDisposable
{
    static string PipeName => "ai-pulse-ui-"+J.Hash(OperatingSystem.IsWindows()?Paths.Root.ToUpperInvariant():Paths.Root)[..24];
    readonly CancellationTokenSource stop=new();
    readonly Task listener;
    public InstanceActivation(Action show)
    {
        listener=Task.Run(async()=>
        {
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(2000);
                    var message=new byte[1];if(await pipe.ReadAsync(message,timeout.Token)==1&&message[0]==1)Dispatcher.UIThread.Post(show);
                }
                catch(OperationCanceledException){}
                catch(Exception e){Paths.Log("window-activation",e);try{await Task.Delay(500,stop.Token);}catch(OperationCanceledException){}}
            }
        });
    }
    public static bool TryShow()
    {
        try{using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.Out,PipeOptions.CurrentUserOnly);pipe.Connect(4000);pipe.WriteByte(1);pipe.Flush();return true;}catch{return false;}
    }
    public void Dispose(){stop.Cancel();try{listener.Wait(1000);}catch{}stop.Dispose();}
}
