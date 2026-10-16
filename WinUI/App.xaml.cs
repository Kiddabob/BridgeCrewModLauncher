using Microsoft.UI.Xaml;
namespace BridgeCrew.Launcher;
public partial class App : Application
{
    private MainWindow? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { AppPaths.Log(e.Exception.ToString()); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(); window.Activate();
    }
}
