using System.Diagnostics;
using System.Windows;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple;

public partial class App : Application
{
    private static readonly TimeSpan ErrorDialogQuietPeriod = TimeSpan.FromSeconds(15);

    private static Mutex? singleInstanceLock;
    private static bool errorDialogOpen;
    private static DateTime lastErrorDialogUtc = DateTime.MinValue;
    private static int suppressedErrorCount;

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
        ReleaseSingleInstanceLock();
        base.OnExit(e);
    }

    /// <summary>Starts a fresh copy and closes this one (used when a setting needs a new speech engine).</summary>
    public static void Restart()
    {
        string? executablePath = Environment.ProcessPath;
        if (executablePath == null) return;
        // The new copy must not find this one's single-instance lock.
        ReleaseSingleInstanceLock();
        Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = false });
        Current.Shutdown();
    }

    private static void ReleaseSingleInstanceLock()
    {
        if (singleInstanceLock == null) return;
        try
        {
            singleInstanceLock.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; disposing still closes the handle.
        }
        singleInstanceLock.Dispose();
        singleInstanceLock = null;
    }

    public App()
    {
        // Unexpected errors are always logged. A dialog is shown at most once per quiet period and never on top of
        // another one, so an error that repeats (for example on every frame) cannot bury the window in dialogs.
        DispatcherUnhandledException += (_, args) =>
        {
            AppHealth.LogError(args.Exception, "Unexpected error (window thread)");
            args.Handled = true;
            if (errorDialogOpen || DateTime.UtcNow - lastErrorDialogUtc < ErrorDialogQuietPeriod)
            {
                suppressedErrorCount++;
                return;
            }

            string alsoLogged = suppressedErrorCount > 0
                ? $"\n\n({suppressedErrorCount} more problem{(suppressedErrorCount == 1 ? " was" : "s were")} logged since the last message.)"
                : "";
            suppressedErrorCount = 0;
            errorDialogOpen = true;
            try
            {
                MessageBox.Show(
                    "Something unexpected went wrong.\n\n" + Redaction.Scrub(args.Exception.Message)
                    + "\n\nTechnical details were saved to:\n" + AppHealth.LogPath
                    + "\n\nIf this keeps happening, please attach that file when you report the problem." + alsoLogged,
                    "Meet Simple - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                errorDialogOpen = false;
                lastErrorDialogUtc = DateTime.UtcNow;
            }
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
