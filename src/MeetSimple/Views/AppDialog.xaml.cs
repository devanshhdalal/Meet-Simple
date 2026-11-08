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
        var dialog = Create(owner, title, message, confirm: false);
        dialog.ShowDialog();
    }

    public static bool Confirm(Window? owner, string title, string message)
    {
        var dialog = Create(owner, title, message, confirm: true);
        dialog.ShowDialog();
        return dialog.confirmed;
    }

    private static AppDialog Create(Window? owner, string title, string message, bool confirm)
    {
        var dialog = new AppDialog
        {
            Owner = owner,
            Title = title,
        };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = message;
        if (confirm)
        {
            dialog.PrimaryButton.Content = "Continue";
            dialog.SecondaryButton.Content = "Cancel";
            dialog.SecondaryButton.Visibility = Visibility.Visible;
            dialog.SecondaryButton.IsCancel = true;
        }
        else
        {
            dialog.PrimaryButton.Content = "OK";
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
