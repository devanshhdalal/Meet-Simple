using System.Diagnostics;
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
        GlossaryBox.Text = settings.Glossary;

        string summaryLabel = Catalog.LabelFor(Catalog.SummaryModels, settings.SummaryModel);
        if (!SummaryModelBox.Items.Contains(summaryLabel)) SummaryModelBox.Items.Add(summaryLabel);
        SummaryModelBox.SelectedItem = summaryLabel;
        LanguageBox.Text = settings.Language;

        foreach (var option in ThemeOptions) ThemeBox.Items.Add(option.Label);
        SyncThemeBox();
        // The theme can also be switched from the top bar while this screen is open.
        Loaded += (_, _) => ThemeManager.ThemeChanged += SyncThemeBox;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= SyncThemeBox;
    }

    private const string BestAccuracyLabel = "Best accuracy (slower, recommended)";
    private const string FasterLabel = "Faster";

    private static readonly (ThemeChoice Choice, string Label)[] ThemeOptions =
    {
        (ThemeChoice.System, "Match Windows (default)"),
        (ThemeChoice.Light, "Light"),
        (ThemeChoice.Dark, "Dark"),
    };

    private bool isSyncingTheme;

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
        string languageCode = LanguageBox.Text.Trim();
        if (!InputValidation.IsValidLanguage(languageCode))
        {
            MessageBox.Show("Please enter a language code such as en, es or pt-BR, or leave it empty to detect it automatically.",
                "Language", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = AppSettings.Current;
        settings.SpeechModel = Catalog.IdFor(Catalog.SpeechModels, SpeechModelBox.SelectedItem as string ?? "", Catalog.DefaultSpeechModel);
        settings.BestAccuracy = (AccuracyBox.SelectedItem as string) != FasterLabel;
        settings.Glossary = InputValidation.CleanGlossary(GlossaryBox.Text);
        settings.SummaryModel = Catalog.IdFor(Catalog.SummaryModels, SummaryModelBox.SelectedItem as string ?? "", "llama3.1:8b");
        settings.Language = languageCode;
        settings.Save();
        shell.ShowHome();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.RecordingsRoot);
        Process.Start(new ProcessStartInfo(AppSettings.RecordingsRoot) { UseShellExecute = true });
    }
}
