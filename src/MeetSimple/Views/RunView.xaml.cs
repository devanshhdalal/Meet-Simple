using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>
/// The progress screen. It only watches a JobSession: closing this screen does not stop the job, and opening it again
/// (from the activity island) shows the job exactly where it is.
/// </summary>
public partial class RunView : UserControl, IDisposable
{
    private readonly MainWindow shell;
    private readonly JobSession job;
    private readonly Dictionary<string, StepRowView> rows = new();
    private readonly DispatcherTimer elapsedTimer;
    private string failureDetails = "";
    private bool isDisposed;

    public RunView(MainWindow shell, JobSession job)
    {
        this.shell = shell;
        this.job = job;
        InitializeComponent();
        StepStrip.Set(job.Request.Mode, "Processing");

        var request = job.Request;
        SubtitleText.Text = request.Tracks.Count == 1 && request.Tracks[0].Speaker == null
            ? Path.GetFileName(request.Tracks[0].SourcePaths[0])
            : "Your recorded meeting";

        foreach (var (id, title) in job.Steps)
        {
            var row = new StepRowView(title);
            rows[id] = row;
            StepsPanel.Children.Add(row);
            if (job.LatestFor(id) is { } latest) row.SetState(latest.State, latest.Detail, latest.Fraction);
        }
        OverallProgress.Value = job.OverallFraction * 100.0;
        OverallLabel.Text = job.StatusLabel;

        elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) => UpdateElapsed();
        UpdateElapsed();

        job.StepChanged += OnStepChanged;
        job.Finished += OnFinished;
        if (job.Status == JobStatus.Running) elapsedTimer.Start();
        else if (job.Status == JobStatus.Failed) ShowFailure();
    }

    /// <summary>Called by the main window when it moves on to another screen. The job itself keeps running.</summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        elapsedTimer.Stop();
        job.StepChanged -= OnStepChanged;
        job.Finished -= OnFinished;
    }

    private void UpdateElapsed() => ElapsedText.Text = "Elapsed: " + FormatDuration(job.Elapsed);

    private void OnStepChanged(StepUpdate update)
    {
        if (isDisposed) return;
        if (rows.TryGetValue(update.StepId, out var row))
        {
            row.SetState(update.State, update.Detail, update.Fraction);
        }
        Motion.AnimateValue(OverallProgress, job.OverallFraction * 100.0);
        OverallLabel.Text = job.StatusLabel;
    }

    private void OnFinished()
    {
        // The window may already have moved on to the result screen, which disposes this one.
        if (isDisposed) return;
        elapsedTimer.Stop();
        UpdateElapsed();
        if (job.Status == JobStatus.Failed) ShowFailure();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        job.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling...";
        MessageText.Text = "Stopping after the current step...";
    }

    private void ShowFailure()
    {
        var error = job.Error;
        failureDetails = Redaction.Scrub(error?.ToString() ?? "Unknown error");
        MessageText.Text = "Something went wrong: " + (error?.Message ?? "unknown error")
            + "\nTechnical details were saved to: " + AppHealth.LogPath;
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");

        ButtonBar.Children.Clear();
        var copyButton = new Button { Content = "Copy error details", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(0, 0, 10, 0) };
        copyButton.Click += (_, _) => CopyToClipboard(failureDetails);
        ButtonBar.Children.Add(copyButton);

        // Whatever was transcribed before the failure was written to a safety copy as it happened.
        if (job.PartialTranscriptPath is { } partialPath && File.Exists(partialPath))
        {
            var partialButton = new Button { Content = "Open what was transcribed", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(0, 0, 10, 0) };
            partialButton.Click += (_, _) => OpenFile(partialPath);
            ButtonBar.Children.Add(partialButton);
        }

        var backButton = new Button { Content = "Back to start", Style = (Style)FindResource("AccentButton") };
        backButton.Click += (_, _) =>
        {
            shell.DismissJob();
            shell.ShowHome();
        };
        ButtonBar.Children.Add(backButton);
    }

    internal static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another program is holding the clipboard; copying is not worth an error dialog.
        }
    }

    internal static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            AppDialog.Notice(Application.Current.MainWindow, "Meet Simple", "The file could not be opened: " + error.Message);
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} h {duration.Minutes} min"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} min {duration.Seconds} s" : $"{duration.Seconds} s";
}
