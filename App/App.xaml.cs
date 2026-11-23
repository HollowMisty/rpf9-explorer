using System.Windows;

namespace RpfPreview.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppTheme.Initialize();
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
