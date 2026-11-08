using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MeetSimple.Core;

namespace MeetSimple.Views;

public partial class ResultView : UserControl
{
    private const int PreviewCharacterLimit = 8000;
    private readonly MainWindow shell;
    private readonly JobResult result;

    public ResultView(MainWindow shell, JobResult result)
    {
        this.shell = shell;
        this.result = result;
        InitializeComponent();

        SubtitleText.Text = $"Finished in {FormatDuration(result.Elapsed)}. Saved in: {Path.GetDirectoryName(result.TranscriptPath)}";
        if (!string.IsNullOrEmpty(result.SummaryError))
        {
            WarningText.Text = "The summary was skipped: " + result.SummaryError;
            WarningText.Visibility = Visibility.Visible;
        }

        if (result.SummaryPath != null)
        {
            HeadingText.Text = "Summary";
            PreviewBox.Text = result.SummaryText;
        }
        else
        {
            HeadingText.Text = "Transcript preview";
            PreviewBox.Text = BuildTranscriptPreview();
        }

        AddButtons();
    }

    private string BuildTranscriptPreview()
    {
        string text = File.ReadAllText(result.TranscriptPath);
        if (text.Length == 0) return "(No speech was detected.)";
        return text.Length > PreviewCharacterLimit
            ? text[..PreviewCharacterLimit] + "\n\n... (open the transcript to read everything)"
            : text;
    }

    private void AddButtons()
    {
        if (result.SummaryPath != null)
        {
            AddButton("Open summary", "AccentButton", () => OpenFile(result.SummaryPath));
            AddButton("Open transcript", "QuietButton", () => OpenFile(result.TranscriptPath));
            AddButton("Copy summary", "QuietButton", () => Clipboard.SetText(result.SummaryText));
        }
        else
        {
            AddButton("Open transcript", "AccentButton", () => OpenFile(result.TranscriptPath));
        }
        AddButton("Show in folder", "QuietButton", () =>
            Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                $"/select,\"{result.TranscriptPath}\"")));
        AddButton("Start another", "QuietButton", shell.ShowHome);
    }

    private void AddButton(string text, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(styleKey),
            Margin = new Thickness(0, 0, 10, 8),
        };
        button.Click += (_, _) => onClick();
        ButtonPanel.Children.Add(button);
    }

    private static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} h {duration.Minutes} min"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} min {duration.Seconds} s" : $"{duration.Seconds} s";
}
