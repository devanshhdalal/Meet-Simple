using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetSimple.Core;

namespace MeetSimple.Views;

public partial class RunView : UserControl, IDisposable
{
    private readonly MainWindow shell;
    private readonly JobRequest request;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<string, StepRowView> rows = new();
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly DispatcherTimer elapsedTimer;
    private string failureDetails = "";

    /// <summary>Called by the main window when it moves on to another screen.</summary>
    public void Dispose()
    {
        elapsedTimer.Stop();
        cancellation.Dispose();
    }

    public RunView(MainWindow shell, JobRequest request)
    {
        this.shell = shell;
        this.request = request;
        InitializeComponent();

        SubtitleText.Text = request.Tracks.Count == 1 && request.Tracks[0].Speaker == null
            ? Path.GetFileName(request.Tracks[0].SourcePath)
            : "Your recorded meeting";

        foreach (var (id, title) in JobPipeline.StepsFor(request.Mode))
        {
            var row = new StepRowView(title);
            rows[id] = row;
            StepsPanel.Children.Add(row);
        }

        elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) => ElapsedText.Text = "Elapsed: " + FormatDuration(stopwatch.Elapsed);
        elapsedTimer.Start();

        IsRunning = true;
        _ = RunAsync();
    }

    public bool IsRunning { get; private set; }

    public void Cancel() => cancellation.Cancel();

    private async Task RunAsync()
    {
        var pipeline = new JobPipeline(request, update => Dispatcher.InvokeAsync(() => Apply(update)));
        try
        {
            var result = await Task.Run(() => pipeline.RunAsync(cancellation.Token));
            Finish();
            shell.ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            Finish();
            shell.ShowHome();
        }
        catch (Exception error)
        {
            Finish();
            ShowFailure(error);
        }
    }

    private void Finish()
    {
        IsRunning = false;
        elapsedTimer.Stop();
    }

    private void Apply(StepUpdate update)
    {
        if (rows.TryGetValue(update.StepId, out var row))
        {
            row.SetState(update.State, update.Detail, update.Fraction);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        cancellation.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling...";
        MessageText.Text = "Stopping after the current step...";
    }

    private void ShowFailure(Exception error)
    {
        failureDetails = Redaction.Scrub(error.ToString());
        MessageText.Text = "Something went wrong: " + error.Message;
        MessageText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");

        ButtonBar.Children.Clear();
        var copyButton = new Button { Content = "Copy error details", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(0, 0, 10, 0) };
        copyButton.Click += (_, _) => Clipboard.SetText(failureDetails);
        var backButton = new Button { Content = "Back to start", Style = (Style)FindResource("AccentButton") };
        backButton.Click += (_, _) => shell.ShowHome();
        ButtonBar.Children.Add(copyButton);
        ButtonBar.Children.Add(backButton);
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} h {duration.Minutes} min"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} min {duration.Seconds} s" : $"{duration.Seconds} s";
}
