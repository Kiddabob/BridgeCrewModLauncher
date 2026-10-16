using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace BridgeCrew.Launcher;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if(args is ["--update-smoke",var output])
        {
            ExerciseUpdate(output).GetAwaiter().GetResult(); return;
        }
        // Native WinUI initialization stays after the updater's early lifecycle hooks.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
    private static async Task ExerciseUpdate(string output)
    {
        try
        {
            var service=new UpdateService(); var status=await service.Check();
            if(!service.IsInstalled||!service.HasUpdate)throw new InvalidOperationException("Installed app must have a newer GitHub release to test an update. "+status);
            await service.Download(_=>{},CancellationToken.None);
            await File.WriteAllTextAsync(output,System.Text.Json.JsonSerializer.Serialize(new{from=service.InstalledVersion,downloaded=true,status}));
            service.Apply("--verify-update",output+".after.json");
        }
        catch(Exception ex){await File.WriteAllTextAsync(output,System.Text.Json.JsonSerializer.Serialize(new{error=ex.ToString()}));}
    }
}
