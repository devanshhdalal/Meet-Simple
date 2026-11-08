using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>One line in the progress list: icon, title, detail text and a progress bar while active.</summary>
public sealed class StepRowView : Grid
{
    private readonly TextBlock iconText = new() { FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock titleText = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock detailText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
    private readonly ProgressBar progressBar = new() { Maximum = 100, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    public StepRowView(string title)
    {
        Margin = new Thickness(0, 9, 0, 9);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        titleText.Text = title;
        SetColumn(titleText, 1);
        SetColumn(detailText, 2);
        SetRow(progressBar, 1);
        SetColumn(progressBar, 1);
        SetColumnSpan(progressBar, 2);

        Children.Add(iconText);
        Children.Add(titleText);
        Children.Add(detailText);
        Children.Add(progressBar);
        SetState(StepState.Pending, "", null);
    }

    public void SetState(StepState state, string detail, double? fraction)
    {
        (string symbol, string brushKey) = state switch
        {
            StepState.Active => ("●", "AccentTextBrush"),
            StepState.Done => ("✓", "SuccessBrush"),
            StepState.Failed => ("✗", "ErrorBrush"),
            StepState.Skipped => ("–", "MutedBrush"),
            _ => ("○", "MutedBrush"),
        };
        iconText.Text = symbol;
        iconText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

        bool emphasize = state == StepState.Active || state == StepState.Done;
        titleText.SetResourceReference(TextBlock.ForegroundProperty, emphasize ? "TextBrush" : "MutedBrush");
        titleText.FontWeight = state == StepState.Active ? FontWeights.SemiBold : FontWeights.Normal;

        detailText.Text = detail;
        detailText.ToolTip = string.IsNullOrEmpty(detail) ? null : detail;
        detailText.SetResourceReference(TextBlock.ForegroundProperty, state == StepState.Failed ? "ErrorBrush" : "MutedBrush");

        if (state != StepState.Active)
        {
            progressBar.Visibility = Visibility.Collapsed;
            progressBar.IsIndeterminate = false;
            return;
        }

        bool wasHidden = progressBar.Visibility != Visibility.Visible;
        progressBar.Visibility = Visibility.Visible;
        if (wasHidden) Motion.FadeIn(progressBar);
        if (fraction == null)
        {
            progressBar.IsIndeterminate = true;
        }
        else
        {
            progressBar.IsIndeterminate = false;
            Motion.AnimateValue(progressBar, Math.Clamp(fraction.Value, 0.0, 1.0) * 100);
        }
    }

}
