using System.Windows;
using MeetSimple.Theming;

namespace MeetSimple;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Pick light or dark (following Windows by default) before any window is created, so the first frame is right.
        ThemeManager.Initialize();
        SpaceManager.Initialize();
        base.OnStartup(e);
    }

    public App()
    {
        // Show unexpected errors in a window instead of closing silently.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(Core.Redaction.Scrub(args.Exception.ToString()), "Meet Simple - unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
