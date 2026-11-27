using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace ImageStudio.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): Velopack must run first so it can
/// handle install / update / uninstall hooks before any UI starts. Then only one instance
/// may run: a second launch wakes the running window and exits.
/// </summary>
public static class Program
{
    const string MutexName = @"Local\VertexAIStudio.SingleInstance";
    const string ShowEventName = @"Local\VertexAIStudio.Show";

    static Mutex? _instance;
    static EventWaitHandle? _showEvent;

    [STAThread]
    static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        _instance = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask that instance to come to the front, then leave.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>Calls <paramref name="onShow"/> (on a background thread) whenever a second launch is attempted.</summary>
    public static void ListenForShowRequests(Action onShow)
    {
        var ev = _showEvent;
        if (ev == null) return;
        new Thread(() =>
        {
            while (true)
            {
                try { ev.WaitOne(); } catch { return; }
                onShow();
            }
        }) { IsBackground = true, Name = "single-instance" }.Start();
    }

    /// <summary>Lets a new instance start (used right before the app restarts itself).</summary>
    public static void ReleaseSingleInstance()
    {
        try { _instance?.ReleaseMutex(); } catch { }
        _instance?.Dispose();
        _instance = null;
        _showEvent?.Dispose();
        _showEvent = null;
    }
}
