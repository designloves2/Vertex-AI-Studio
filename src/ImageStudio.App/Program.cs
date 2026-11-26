using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace ImageStudio.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): Velopack must run first so it can
/// handle install / update / uninstall hooks before any UI starts.
/// </summary>
public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
