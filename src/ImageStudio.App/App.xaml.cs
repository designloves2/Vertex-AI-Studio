using Microsoft.UI.Xaml;

namespace ImageStudio.App;

public partial class App : Application
{
    MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VertexAIStudio", "crash.log");
                File.AppendAllText(log, $"[{DateTime.Now:s}] {e.Exception}\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
