using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetSimple.Core;
using MeetSimple.Theming;
using MeetSimple.Views;

namespace MeetSimple;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The job is disposed in ClearJob; a window's lifetime ends with Closed, not Dispose.")]
public partial class MainWindow : Window
{
    /// <summary>Room kept free at the bottom of the card while the activity island is showing.</summary>
    private const double IslandClearance = 76.0;

    private JobSession? activeJob;
    private bool quitRequested;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this, "BackgroundBrush");
        ThemeManager.Transition = PlayThemeTransition;
        SpaceManager.Transition = PlayThemeTransition;
        ThemeManager.ThemeChanged += OnThemeOrSpaceChromeChanged;
        SpaceManager.SpaceChanged += OnThemeOrSpaceChromeChanged;
        Island.Activated += OnIslandActivated;
        UpdateThemeToggle();
        RebuildSpaceDots();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        CardContent.SizeChanged += OnCardSizeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.Transition = null;
            SpaceManager.Transition = null;
            ThemeManager.ThemeChanged -= OnThemeOrSpaceChromeChanged;
            SpaceManager.SpaceChanged -= OnThemeOrSpaceChromeChanged;
        };
        ShowHome();
    }

    /// <summary>True while a transcription or summary is running (other screens can still be viewed).</summary>
    public bool IsJobRunning => activeJob?.Status == JobStatus.Running;

    private bool IsRecording => Host.Content is RecordView { IsRecording: true };

    /// <summary>True while the Record screen is recording (a call recorded from the island does not count).</summary>
    public bool IsRecordingInApp => IsRecording;

    private void OnThemeOrSpaceChromeChanged()
    {
        UpdateThemeToggle();
        RebuildSpaceDots();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Motion.FadeIn(RootGrid, Motion.Gentle);

        // First launch: offer the tour. It can always be skipped and replayed later.
        if (!AppSettings.Current.TutorialCompleted)
        {
            ShowTutorial();
        }

        if (App.GraphicsCardTurnedOffAfterCrash)
        {
            App.GraphicsCardTurnedOffAfterCrash = false;
            AppDialog.Notice(this, "Graphics card turned off",
                "Meet Simple closed unexpectedly while transcribing with the graphics card, so it now uses the processor only. "
                + "Transcribing will be slower but more reliable. You can turn the graphics card back on in Settings.");
        }

        await OfferUnfinishedRecordingAsync();
    }

    /// <summary>A recording that was never transcribed (saved for later, window closed, crash) is offered once per start.</summary>
    private async Task OfferUnfinishedRecordingAsync()
    {
        string? folderBeingRecorded = App.Calls?.RecordingFolder;
        var unfinished = await Task.Run(() => RecordingRecovery.FindLatest(folderBeingRecorded));
        if (unfinished == null || activeJob != null) return;
        await OfferRecordingAsync(unfinished);
    }

    public async Task OfferRecordingAsync(UnfinishedRecording recording)
    {
        string when = recording.StartedAt.ToString("dddd d MMMM 'at' HH:mm");
        int minutes = Math.Max(1, (int)Math.Round(recording.Duration.TotalMinutes));
        string question = recording.Mode == JobMode.TranscribeAndSummarize ? "Summarize it now?" : "Transcribe it now?";
        bool start = AppDialog.Confirm(this, "A recording is waiting",
            $"Your recording from {when} ({minutes} min) hasn't been transcribed yet.\n\n{question} "
            + "If you cancel, the audio stays in your Recordings folder.");
        if (!start)
        {
            RecordingRecovery.Clear(recording.Folder);
            return;
        }
        var request = CreateRequest(recording.Mode, recording.Tracks, recording.Folder, "Meeting", recording.Folder);
        await StartJobAsync(request);
    }

    // ----- tray -----

    /// <summary>Brings the window back from the tray (or from behind other windows).</summary>
    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Quits for real (tray menu). The usual "something is still running" questions still apply.</summary>
    public void RequestQuit()
    {
        App.Calls?.StopRecordingForExit();
        quitRequested = true;
        if (!IsVisible && (IsRecording || IsJobRunning)) ShowFromTray();
        Close();
        if (IsVisible) quitRequested = false; // The person chose to keep it running.
    }

    /// <summary>The next close really closes (restart, Windows signing out), instead of hiding in the tray.</summary>
    public void AllowExit() => quitRequested = true;

    private void HideToTray()
    {
        Hide();
        var settings = AppSettings.Current;
        if (settings.TrayNoticeShown) return;
        settings.TrayNoticeShown = true;
        settings.Save();
        App.Calls?.ShowTrayNotice("Still watching for calls", "Meet Simple is in the tray. Right-click it to quit.");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Watching only works while Meet Simple runs, so the close button tucks it into the tray instead.
        // A running job keeps going there too; only an in-app recording needs its own answer below.
        if (!quitRequested && AppSettings.Current.WatchForCalls && !IsRecording)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (IsRecording)
        {
            if (!AppDialog.Confirm(this, "Stop recording and exit?",
                    "The recording so far will be saved, and Meet Simple will offer to transcribe it the next time you open it."))
            {
                e.Cancel = true;
                return;
            }
            // Disposing the screen stops the recorder, which finishes the audio files properly.
            (Host.Content as IDisposable)?.Dispose();
            Host.Content = null;
        }
        else if (IsJobRunning)
        {
            if (!AppDialog.Confirm(this, "Meet Simple", "A transcription is still running. Stop it and exit?"))
            {
                e.Cancel = true;
                return;
            }
            activeJob?.Cancel();
        }
        // Exiting on purpose must not look like a graphics-card crash at the next start.
        AppHealth.ClearGpuAttempt();
    }

    // ----- navigation -----
    private void Navigate(UserControl view, bool openedFromIsland = false)
    {
        bool animate = Motion.Enabled && IsLoaded && Host.Content != null;
        // Leaving the progress screen while the job still matters: the page shrinks into the island instead of fading.
        bool tuckIntoIsland = Host.Content is RunView && view is not RunView && activeJob != null && !openedFromIsland;
        var snapshot = animate ? Motion.Capture(Host, (Brush)FindResource("BackgroundBrush")) : null;

        (Host.Content as IDisposable)?.Dispose();
        Host.Content = view;
        UpdateNavSelection();

        if (tuckIntoIsland)
        {
            RefreshIslandContent();
            Island.ShrinkFrom(snapshot);
            UpdateHostClearance();
            Motion.RiseIn(view);
            return;
        }
        RefreshIsland();

        if (snapshot == null) return;
        PageSnapshot.Source = snapshot;
        PageSnapshot.Visibility = Visibility.Visible;
        Motion.FadeOut(PageSnapshot, Motion.Quick, () =>
        {
            PageSnapshot.Visibility = Visibility.Collapsed;
            PageSnapshot.Source = null;
        });
        Motion.RiseIn(view);
    }

    // ----- activity island -----

    private void RefreshIsland()
    {
        bool shouldShow = activeJob != null && activeJob.Status != JobStatus.Cancelled && Host.Content is not RunView;
        if (shouldShow && !Island.IsShown)
        {
            RefreshIslandContent();
            Island.PopIn();
        }
        else if (!shouldShow && Island.IsShown)
        {
            Island.PopOut();
        }
        UpdateHostClearance();
    }

    private void UpdateHostClearance() =>
        Host.Margin = new Thickness(0, 0, 0, Island.IsShown ? IslandClearance : 0);

    private void RefreshIslandContent()
    {
        var job = activeJob;
        if (job == null) return;
        switch (job.Status)
        {
            case JobStatus.Succeeded:
                bool hasSummary = job.Result?.SummaryPath != null;
                Island.Update(IslandState.Done, hasSummary ? "Summary ready" : "Transcript ready", "Click to open", 1.0);
                break;
            case JobStatus.Failed:
                Island.Update(IslandState.Failed, "Couldn't finish", "Click for details", job.OverallFraction);
                break;
            case JobStatus.Running:
                // Each fact appears once: the title says what is happening, the ring shows how far that step is,
                // and the hover line only adds something new (time left, megabytes, which part).
                string title = IslandTitleFor(job.ActiveStepId);
                Island.Update(IslandState.Working, title, IslandDetailFrom(job.StatusLabel, title),
                    job.ActiveStepFraction ?? job.OverallFraction);
                break;
        }
    }

    private static string IslandTitleFor(string? stepId) => stepId switch
    {
        "speech_model" => "Preparing",
        "ollama" => "Setting up summaries",
        "ai_model" => "Getting the summary AI",
        "audio" => "Reading audio",
        "transcribe" => "Transcribing",
        "summary" => "Summarizing",
        _ => "Getting ready",
    };

    /// <summary>
    /// Keeps only what the title and the ring do not already say: a leading percentage is dropped (the ring shows it),
    /// and a line that merely restates the title ("Reading audio...", "Transcribe") is left out altogether.
    /// </summary>
    private static string IslandDetailFrom(string statusLabel, string title)
    {
        string detail = statusLabel.Trim();
        int percentEnd = detail.IndexOf('%');
        if (percentEnd > 0 && detail[..percentEnd].All(char.IsDigit))
        {
            detail = detail[(percentEnd + 1)..].TrimStart(' ', '-');
        }
        if (detail.Length == 0) return "";

        static string Comparable(string text) => text.Trim().TrimEnd('.', '…').ToLowerInvariant();
        string comparableDetail = Comparable(detail);
        string comparableTitle = Comparable(title);
        if (comparableTitle.StartsWith(comparableDetail, StringComparison.Ordinal)
            || comparableDetail.StartsWith(comparableTitle, StringComparison.Ordinal)
            || comparableDetail is "getting ready" or "finishing up" or "stopped")
        {
            return "";
        }
        return char.ToUpperInvariant(detail[0]) + detail[1..];
    }

    private void OnIslandActivated()
    {
        var job = activeJob;
        if (job == null) return;
        if (ShowBusyNotice()) return;

        Island.ExpandAway();
        if (job.Status == JobStatus.Succeeded && job.Result is { } result)
        {
            ClearJob();
            Navigate(new ResultView(this, result), openedFromIsland: true);
        }
        else
        {
            Navigate(new RunView(this, job), openedFromIsland: true);
        }
        UpdateHostClearance();
    }

    // ----- jobs -----

    private void OnJobStepChanged(StepUpdate update) => RefreshIslandContent();

    private void OnJobFinished()
    {
        var job = activeJob;
        if (job == null) return;
        RefreshIslandContent();

        if (job.Status == JobStatus.Succeeded && Host.Content is RunView && job.Result is { } result)
        {
            ClearJob();
            ShowResult(result);
        }
        else if (job.Status == JobStatus.Cancelled)
        {
            ClearJob();
            if (Host.Content is RunView) ShowHome(); else RefreshIsland();
        }
        // A success seen from another screen, or a failure, stays in the island until it is opened.
    }

    /// <summary>Forgets the current job (finished, failed and acknowledged, or replaced by a new one).</summary>
    public void DismissJob()
    {
        ClearJob();
        RefreshIsland();
    }

    private void ClearJob()
    {
        if (activeJob == null) return;
        activeJob.StepChanged -= OnJobStepChanged;
        activeJob.Finished -= OnJobFinished;
        activeJob.Dispose();
        activeJob = null;
    }

    // ----- theme -----
    private void PlayThemeTransition(Action applyTheme)
    {
        var snapshot = Motion.Enabled && IsLoaded ? Motion.Capture(RootGrid, Background) : null;
        if (snapshot == null)
        {
            applyTheme();
            return;
        }

        // Paint the old look over the window, switch underneath, then fade the old look away.
        ThemeSnapshot.Source = snapshot;
        ThemeSnapshot.Visibility = Visibility.Visible;
        applyTheme();
        Motion.FadeOut(ThemeSnapshot, Motion.Gentle, () =>
        {
            ThemeSnapshot.Visibility = Visibility.Collapsed;
            ThemeSnapshot.Source = null;
        });
    }

    private void UpdateThemeToggle()
    {
        // Shows the icon of the mode you would switch to: a moon in light mode, a sun in dark mode.
        ThemeToggle.Content = ThemeManager.IsDark ? "" : "";
        ThemeToggle.ToolTip = ThemeManager.IsDark ? "Switch to light mode" : "Switch to dark mode";
        System.Windows.Automation.AutomationProperties.SetName(ThemeToggle, (string)ThemeToggle.ToolTip);
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e) =>
        ThemeManager.SetChoice(ThemeManager.IsDark ? ThemeChoice.Light : ThemeChoice.Dark);

    // ----- window chrome (the standard title bar is replaced by the sidebar and these buttons) -----
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A maximised window with a custom frame extends past the screen edge by the resize border, so pull the content back in.
        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    /// <summary>The card has rounded corners, so everything inside it (including fading snapshots) is clipped to the same shape.</summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double cornerRadius = 13.0;
        CardContent.Clip = new RectangleGeometry(new Rect(0, 0, CardContent.ActualWidth, CardContent.ActualHeight), cornerRadius, cornerRadius);
    }

    // ----- sidebar -----
    private void RebuildSpaceDots() => PopulateSpaceDots(SpaceDots, "Space");

    /// <summary>Builds theme-aware space swatches into a panel (sidebar or Settings).</summary>
    public static void PopulateSpaceDots(Panel host, string groupName)
    {
        host.Children.Clear();
        var dotStyle = (Style)host.FindResource("SpaceDotButton");
        bool dark = ThemeManager.IsDark;
        foreach (var space in SpaceManager.Spaces)
        {
            string spaceId = space.Id;
            Color start = dark ? space.DarkStart : space.LightStart;
            Color accent = dark ? space.DarkAccent : space.LightAccent;
            var brush = new LinearGradientBrush(start, accent, 45.0);
            brush.Freeze();
            var dot = new RadioButton
            {
                Style = dotStyle,
                GroupName = groupName,
                Background = brush,
                ToolTip = space.Label + " space",
                IsChecked = spaceId == SpaceManager.Current.Id,
                Margin = new Thickness(0, 0, 4, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(dot, space.Label + " space");
            dot.Click += (_, _) => SpaceManager.SetSpace(spaceId);
            host.Children.Add(dot);
        }
    }

    private void UpdateNavSelection()
    {
        NavHome.IsChecked = Host.Content is HomeView;
        NavSettings.IsChecked = Host.Content is SettingsView;
    }

    /// <summary>Recording owns this screen (leaving it would stop the recording), so navigation waits for Stop.</summary>
    private bool ShowBusyNotice()
    {
        if (!IsRecording) return false;
        AppDialog.Notice(this, "Meet Simple", "Please stop the recording first.");
        UpdateNavSelection();
        return true;
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (ShowBusyNotice()) return;
        ShowHome();
    }

    private void OnHomeNavClick(object sender, RoutedEventArgs e)
    {
        if (ShowBusyNotice()) return;
        if (Host.Content is HomeView) return;
        ShowHome();
    }

    private void OnPinnedTranscribeClick(object sender, RoutedEventArgs e)
    {
        if (ShowBusyNotice()) return;
        ShowSource(JobMode.TranscribeOnly);
    }

    private void OnPinnedSummarizeClick(object sender, RoutedEventArgs e)
    {
        if (ShowBusyNotice()) return;
        ShowSource(JobMode.TranscribeAndSummarize);
    }

    private void OnPinnedFolderClick(object sender, RoutedEventArgs e) => OpenRecordingsFolder();

    public static void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.RecordingsRoot);
            Process.Start(new ProcessStartInfo(AppSettings.RecordingsRoot) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppDialog.Notice(Application.Current.MainWindow, "Meet Simple", "The recordings folder could not be opened: " + error.Message);
        }
    }

    public void ShowHome() => Navigate(new HomeView(this));

    /// <summary>Starting something new waits for the running job; looking around (Home, Settings, tour) does not.</summary>
    public void ShowSource(JobMode mode)
    {
        if (IsJobRunning)
        {
            AppDialog.Notice(this, "One thing at a time",
                "A transcription is running. You can look around while it works and start something new once it has finished. "
                + "Its progress is at the bottom of the window.");
            UpdateNavSelection();
            return;
        }
        Navigate(new SourceView(this, mode));
    }

    public void ShowRecord(JobMode mode)
    {
        if (App.Calls?.IsRecording == true)
        {
            AppDialog.Notice(this, "A call is being recorded", "Stop it from the pill at the top of the screen first.");
            UpdateNavSelection();
            return;
        }
        Navigate(new RecordView(this, mode));
    }
    public void ShowSettings() => Navigate(new SettingsView(this));
    public void ShowResult(JobResult result) => Navigate(new ResultView(this, result));

    public void ShowTutorial()
    {
        var tutorial = new TutorialWindow { Owner = this };
        tutorial.ShowDialog();
    }

    private void OnTourClick(object sender, RoutedEventArgs e)
    {
        if (ShowBusyNotice()) return;
        ShowTutorial();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (ShowBusyNotice()) return;
        if (Host.Content is SettingsView) return;
        ShowSettings();
    }

    // ----- starting jobs -----
    public JobRequest CreateRequest(JobMode mode, IReadOnlyList<AudioTrack> tracks, string outputFolder, string baseName,
        string? recoveryFolder = null)
    {
        var settings = AppSettings.Current;
        return new JobRequest
        {
            Mode = mode,
            Tracks = tracks,
            OutputFolder = outputFolder,
            BaseName = baseName,
            SpeechModel = settings.SpeechModel,
            SummaryModel = settings.SummaryModel,
            Language = string.IsNullOrWhiteSpace(settings.Language) ? null : settings.Language,
            Glossary = string.IsNullOrWhiteSpace(settings.Glossary) ? null : settings.Glossary,
            BestAccuracy = settings.BestAccuracy,
            RecoveryFolder = recoveryFolder,
        };
    }

    /// <summary>Asks before any large one-time download, then starts the job. Returns false if it did not start.</summary>
    public async Task<bool> StartJobAsync(JobRequest request)
    {
        if (IsJobRunning)
        {
            AppDialog.Notice(this, "One thing at a time", "Please wait until the current transcription has finished.");
            return false;
        }
        if (!await ConfirmDownloadsAsync(request))
        {
            return false;
        }

        ClearJob(); // A finished or failed earlier job that was never opened is replaced.
        var job = new JobSession(request);
        activeJob = job;
        job.StepChanged += OnJobStepChanged;
        job.Finished += OnJobFinished;
        job.Start();
        Navigate(new RunView(this, job));
        return true;
    }

    /// <summary>
    /// Lists everything this job would download for the first time - the speech model too, which can be 3 GB - and asks.
    /// Whether the summary AI is already installed is read from Ollama's files when its server is not running yet.
    /// </summary>
    private async Task<bool> ConfirmDownloadsAsync(JobRequest request)
    {
        var items = new List<string>();
        if (!File.Exists(SpeechModelStore.PathFor(request.SpeechModel)))
        {
            items.Add($"  •  Speech recognition: {Catalog.LabelFor(Catalog.SpeechModels, request.SpeechModel)}");
        }

        if (request.Mode == JobMode.TranscribeAndSummarize)
        {
            var installedModels = await OllamaClient.TryListModelsAsync();
            bool serverRunning = installedModels != null;
            bool needsOllama = !serverRunning && OllamaSetup.FindExecutable() == null;
            bool needsModel = needsOllama
                              || (serverRunning
                                  ? !OllamaClient.IsInstalled(installedModels!, request.SummaryModel)
                                  : !OllamaClient.IsInstalledOnDisk(request.SummaryModel));
            if (needsOllama) items.Add("  •  Ollama, the engine that runs the summary AI (about 1-2 GB)");
            if (needsModel) items.Add($"  •  Summary AI: {Catalog.LabelFor(Catalog.SummaryModels, request.SummaryModel)}");
        }

        if (items.Count == 0) return true;
        return AppDialog.Confirm(this, "One-time download",
            "Meet Simple needs to download the following once. After that it works offline, "
            + "and nothing you record ever leaves this computer:\n\n"
            + string.Join("\n", items) + "\n\nContinue?");
    }
}
