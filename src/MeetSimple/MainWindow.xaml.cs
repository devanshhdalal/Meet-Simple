using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetSimple.Core;
using MeetSimple.Theming;
using MeetSimple.Views;

namespace MeetSimple;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this, "BackgroundBrush");
        ThemeManager.Transition = PlayThemeTransition;
        SpaceManager.Transition = PlayThemeTransition;
        ThemeManager.ThemeChanged += OnThemeOrSpaceChromeChanged;
        SpaceManager.SpaceChanged += OnThemeOrSpaceChromeChanged;
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

    private void OnThemeOrSpaceChromeChanged()
    {
        UpdateThemeToggle();
        RebuildSpaceDots();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
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
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!IsBusy) return;
        if (!AppDialog.Confirm(this, "Meet Simple", "Something is still running. Stop it and exit?"))
        {
            e.Cancel = true;
            return;
        }
        (Host.Content as RunView)?.Cancel();
        // Exiting on purpose must not look like a graphics-card crash at the next start.
        AppHealth.ClearGpuAttempt();
    }

    private bool IsBusy =>
        Host.Content is RunView { IsRunning: true } || Host.Content is RecordView { IsRecording: true };

    // ----- navigation -----
    private void Navigate(UserControl view)
    {
        // The old screen fades away while the new one eases in, so a screen change is never a sudden jump.
        bool animate = Motion.Enabled && IsLoaded && Host.Content != null;
        var snapshot = animate ? Motion.Capture(Host, (Brush)FindResource("BackgroundBrush")) : null;

        (Host.Content as IDisposable)?.Dispose();
        Host.Content = view;
        UpdateNavSelection();

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
        ThemeToggle.Content = ThemeManager.IsDark ? "\uE706" : "\uE708";
        ThemeToggle.ToolTip = ThemeManager.IsDark ? "Switch to light mode" : "Switch to dark mode";
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
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
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
            dot.Click += (_, _) => SpaceManager.SetSpace(spaceId);
            host.Children.Add(dot);
        }
    }

    private void UpdateNavSelection()
    {
        NavHome.IsChecked = Host.Content is HomeView;
        NavSettings.IsChecked = Host.Content is SettingsView;
    }

    /// <summary>Returns true (after telling the user) when a job is running and navigation should wait.</summary>
    private bool ShowBusyNotice()
    {
        if (!IsBusy) return false;
        AppDialog.Notice(this, "Meet Simple", "Please wait until the current job has finished.");
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

    private void OnPinnedFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.RecordingsRoot);
        Process.Start(new ProcessStartInfo(AppSettings.RecordingsRoot) { UseShellExecute = true });
    }

    public void ShowHome() => Navigate(new HomeView(this));
    public void ShowSource(JobMode mode) => Navigate(new SourceView(this, mode));
    public void ShowRecord(JobMode mode) => Navigate(new RecordView(this, mode));
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
    public JobRequest CreateRequest(JobMode mode, IReadOnlyList<AudioTrack> tracks, string outputFolder, string baseName)
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
        };
    }

    /// <summary>Asks before any large one-time download, then starts the job. Returns false if the user declined.</summary>
    public async Task<bool> StartJobAsync(JobRequest request)
    {
        if (!await ConfirmSetupAsync(request.Mode, request.SummaryModel))
        {
            return false;
        }
        Navigate(new RunView(this, request));
        return true;
    }

    private async Task<bool> ConfirmSetupAsync(JobMode mode, string summaryModel)
    {
        if (mode != JobMode.TranscribeAndSummarize) return true;

        var installedModels = await OllamaClient.TryListModelsAsync();
        bool serverRunning = installedModels != null;
        bool needsOllama = !serverRunning && OllamaSetup.FindExecutable() == null;
        bool needsModel = needsOllama || (serverRunning && !OllamaClient.IsInstalled(installedModels!, summaryModel));
        if (!needsOllama && !needsModel) return true;

        var items = new List<string>();
        if (needsOllama) items.Add("  •  Ollama, the engine that runs the AI (about 1-2 GB)");
        if (needsModel) items.Add($"  •  The AI model \"{summaryModel}\" (see its size in Settings)");

        return AppDialog.Confirm(this, "One-time setup",
            "To write summaries privately on this computer, the following will be downloaded and set up once:\n\n"
            + string.Join("\n", items) + "\n\nContinue?");
    }
}
