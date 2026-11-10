using System.Windows;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>Determinate progress while an in-app update downloads and prepares to apply.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Cancellation is cancelled and disposed when the window closes.")]
public partial class UpdateProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private bool finished;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this, "SurfaceBrush");
        Closed += (_, _) =>
        {
            if (!finished) cancellation.Cancel();
            cancellation.Dispose();
        };
    }

    public CancellationToken Token => cancellation.Token;

    public void Report(ProgressInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Report(info));
            return;
        }

        if (!string.IsNullOrWhiteSpace(info.Detail)) StatusText.Text = info.Detail!;
        if (info.Fraction is double fraction)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(fraction * 100.0, 0, 100);
        }
        else
        {
            Bar.IsIndeterminate = true;
        }
    }

    public void MarkFinished()
    {
        finished = true;
        CancelButton.IsEnabled = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        cancellation.Cancel();
        Close();
    }
}
