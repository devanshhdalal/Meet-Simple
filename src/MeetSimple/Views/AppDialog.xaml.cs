using System.Windows;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>Small themed notice / confirm dialog that matches the floating-card look.</summary>
public partial class AppDialog : Window
{
    private bool confirmed;

    public AppDialog()
    {
        InitializeComponent();
        ThemeManager.Attach(this, "SurfaceBrush");
    }

    public static void Notice(Window? owner, string title, string message)
    {
        var dialog = Create(owner, title, message, confirm: false, primaryLabel: "OK", secondaryLabel: "Cancel");
        dialog.ShowDialog();
    }

    public static bool Confirm(Window? owner, string title, string message) =>
        Confirm(owner, title, message, "Continue", "Cancel");

    public static bool Confirm(
        Window? owner, string title, string message, string primaryLabel, string secondaryLabel)
    {
        var dialog = Create(owner, title, message, confirm: true, primaryLabel, secondaryLabel);
        dialog.ShowDialog();
        return dialog.confirmed;
    }

    private static AppDialog Create(
        Window? owner, string title, string message, bool confirm, string primaryLabel, string secondaryLabel)
    {
        var dialog = new AppDialog
        {
            Owner = owner,
            Title = title,
        };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = message;
        dialog.PrimaryButton.Content = primaryLabel;
        if (confirm)
        {
            dialog.SecondaryButton.Content = secondaryLabel;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
            dialog.SecondaryButton.IsCancel = true;
        }
        else
        {
            dialog.SecondaryButton.Visibility = Visibility.Collapsed;
            dialog.PrimaryButton.IsCancel = true;
        }
        return dialog;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        confirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        confirmed = false;
        DialogResult = false;
        Close();
    }
}
