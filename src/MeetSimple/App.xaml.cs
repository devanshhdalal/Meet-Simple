using System.Windows;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple;

public partial class App : Application
{
    private static Mutex? singleInstanceLock;

    /// <summary>True when the last run died while using the graphics card, so the main window can explain the switch.</summary>
    public static bool GraphicsCardTurnedOffAfterCrash { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Two copies would fight over the microphone, the settings file and the Ollama server.
        singleInstanceLock = new Mutex(initiallyOwned: true, @"Local\MeetSimple.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Meet Simple is already open. Look for it in your taskbar.", "Meet Simple",
                MessageBoxButton.OK, MessageBoxImage.Information);
            singleInstanceLock.Dispose();
            singleInstanceLock = null;
            Shutdown();
            return;
        }

        // If the last run died in the middle of a graphics-card transcription, fall back to the processor.
        if (AppHealth.ConsumeCrashedGpuAttempt() && AppSettings.Current.UseGraphicsCard)
        {
            AppSettings.Current.UseGraphicsCard = false;
            AppSettings.Current.Save();
            GraphicsCardTurnedOffAfterCrash = true;
        }
        TranscriptionService.ConfigureRuntime(AppSettings.Current.UseGraphicsCard);
        _ = Task.Run(AppHealth.CleanOldTempFolders);

        // Pick light or dark (following Windows by default) before any window is created, so the first frame is right.
        ThemeManager.Initialize();
        SpaceManager.Initialize();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstanceLock?.Dispose();
        base.OnExit(e);
    }

    public App()
    {
        // Unexpected errors are written to a log users can attach to a bug report, and shown in a window instead of closing silently.
        DispatcherUnhandledException += (_, args) =>
        {
            AppHealth.LogError(args.Exception, "Unexpected error (window thread)");
            MessageBox.Show(
                "Something unexpected went wrong.\n\n" + Redaction.Scrub(args.Exception.Message)
                + "\n\nTechnical details were saved to:\n" + AppHealth.LogPath
                + "\n\nIf this keeps happening, please attach that file when you report the problem.",
                "Meet Simple - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppHealth.LogError(args.ExceptionObject as Exception, "Unexpected error (background thread)");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppHealth.LogError(args.Exception, "Unobserved background task error");
            args.SetObserved();
        };
    }
}
