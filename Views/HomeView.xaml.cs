using System.Windows.Controls;
using System.Windows.Input;
using MeetSimple.Core;

namespace MeetSimple.Views;

public partial class HomeView : UserControl
{
    private readonly MainWindow shell;

    public HomeView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
    }

    private void OnTranscribeOnlyClick(object sender, MouseButtonEventArgs e) => shell.ShowSource(JobMode.TranscribeOnly);

    private void OnSummarizeClick(object sender, MouseButtonEventArgs e) => shell.ShowSource(JobMode.TranscribeAndSummarize);
}
