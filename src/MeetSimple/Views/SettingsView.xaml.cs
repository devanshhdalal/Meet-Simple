using System.Windows;
using System.Windows.Controls;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

public partial class SettingsView : UserControl
{
    private readonly MainWindow shell;

    public SettingsView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();

        var settings = AppSettings.Current;
        foreach (var choice in Catalog.SpeechModels) SpeechModelBox.Items.Add(choice.Label);
        foreach (var choice in Catalog.SummaryModels) SummaryModelBox.Items.Add(choice.Label);
        SpeechModelBox.SelectedItem = Catalog.LabelFor(Catalog.SpeechModels, settings.SpeechModel);
        AccuracyBox.Items.Add(BestAccuracyLabel);
        AccuracyBox.Items.Add(FasterLabel);
        AccuracyBox.SelectedItem = settings.BestAccuracy ? BestAccuracyLabel : FasterLabel;
        GraphicsBox.Items.Add(UseGraphicsLabel);
        GraphicsBox.Items.Add(ProcessorOnlyLabel);
        GraphicsBox.SelectedItem = settings.UseGraphicsCard ? UseGraphicsLabel : ProcessorOnlyLabel;
        GlossaryBox.Text = settings.Glossary;
        CallWatchBox.Items.Add(WatchCallsLabel);
        CallWatchBox.Items.Add(OffLabel);
        CallWatchBox.SelectedItem = settings.WatchForCalls ? WatchCallsLabel : OffLabel;
        StartupBox.Items.Add(OffLabel);
        StartupBox.Items.Add(StartInTrayLabel);
        StartupBox.SelectedItem = settings.StartWithWindows ? StartInTrayLabel : OffLabel;

        string summaryLabel = Catalog.LabelFor(Catalog.SummaryModels, settings.SummaryModel);
        if (!SummaryModelBox.Items.Contains(summaryLabel)) SummaryModelBox.Items.Add(summaryLabel);
        SummaryModelBox.SelectedItem = summaryLabel;
        foreach (var language in Catalog.Languages) LanguageBox.Items.Add(language.Label);
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Catalog.Languages, language => language.Id == settings.Language));

        foreach (var option in ThemeOptions) ThemeBox.Items.Add(option.Label);
        SyncThemeBox();
        RefreshSpaceDots();

        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnAppearanceChromeChanged;
            SpaceManager.SpaceChanged += OnAppearanceChromeChanged;
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnAppearanceChromeChanged;
            SpaceManager.SpaceChanged -= OnAppearanceChromeChanged;
        };
    }

    private const string BestAccuracyLabel = "Best accuracy (slower, recommended)";
    private const string FasterLabel = "Faster";
    private const string UseGraphicsLabel = "Use the graphics card when available (faster)";
    private const string ProcessorOnlyLabel = "Processor only (slower, most compatible)";
    private const string WatchCallsLabel = "Ask to record when a call starts";
    private const string StartInTrayLabel = "Start in the tray when I sign in";
    private const string OffLabel = "Off";

    private static readonly (ThemeChoice Choice, string Label)[] ThemeOptions =
    {
        (ThemeChoice.System, "Match Windows (default)"),
        (ThemeChoice.Light, "Light"),
        (ThemeChoice.Dark, "Dark"),
    };

    private bool isSyncingTheme;

    private void OnAppearanceChromeChanged()
    {
        SyncThemeBox();
        RefreshSpaceDots();
    }

    private void RefreshSpaceDots() => MainWindow.PopulateSpaceDots(SpaceDotsPanel, "SettingsSpace");

    private void SyncThemeBox()
    {
        isSyncingTheme = true;
        ThemeBox.SelectedIndex = Array.FindIndex(ThemeOptions, option => option.Choice == ThemeManager.Choice);
        isSyncingTheme = false;
    }

    private void OnThemeBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSyncingTheme || ThemeBox.SelectedIndex < 0) return;
        ThemeManager.SetChoice(ThemeOptions[ThemeBox.SelectedIndex].Choice);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => shell.ShowHome();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string languageCode = LanguageBox.SelectedIndex > 0 ? Catalog.Languages[LanguageBox.SelectedIndex].Id : "";
        var settings = AppSettings.Current;
        settings.SpeechModel = Catalog.IdFor(Catalog.SpeechModels, SpeechModelBox.SelectedItem as string ?? "", Catalog.DefaultSpeechModel);
        settings.BestAccuracy = (AccuracyBox.SelectedItem as string) != FasterLabel;
        settings.UseGraphicsCard = (GraphicsBox.SelectedItem as string) != ProcessorOnlyLabel;
        settings.Glossary = InputValidation.CleanGlossary(GlossaryBox.Text);
        settings.SummaryModel = Catalog.IdFor(Catalog.SummaryModels, SummaryModelBox.SelectedItem as string ?? "", "llama3.1:8b");
        settings.Language = languageCode;
        settings.WatchForCalls = (CallWatchBox.SelectedItem as string) == WatchCallsLabel;
        settings.StartWithWindows = (StartupBox.SelectedItem as string) == StartInTrayLabel;
        settings.Save();
        App.Calls?.ApplySettings();

        // The speech engine is chosen once per run, so compare with what is actually loaded, not with the old setting.
        if (settings.UseGraphicsCard != TranscriptionService.GraphicsCardEnabledForThisRun)
        {
            var owner = Window.GetWindow(this);
            if (shell.IsJobRunning)
            {
                AppDialog.Notice(owner, "Restart needed",
                    "The graphics card setting is saved and takes effect the next time you open Meet Simple.");
            }
            else if (AppDialog.Confirm(owner, "Restart Meet Simple?",
                         "The graphics card setting takes effect after a restart. It only takes a moment. Restart now?"))
            {
                App.Restart();
                return;
            }
        }
        shell.ShowHome();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => MainWindow.OpenRecordingsFolder();
}
