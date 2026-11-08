using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MeetSimple.Core;

namespace MeetSimple.Views;

public partial class SourceView : UserControl
{
    private readonly MainWindow shell;
    private readonly JobMode mode;

    public SourceView(MainWindow shell, JobMode mode)
    {
        this.shell = shell;
        this.mode = mode;
        InitializeComponent();
        TitleText.Text = mode == JobMode.TranscribeAndSummarize ? "Transcribe + summarize" : "Transcribe";
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => shell.ShowHome();

    private void OnRecordClick(object sender, MouseButtonEventArgs e) => shell.ShowRecord(mode);

    private async void OnFileClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a recording",
            Filter = "Recordings|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.mp3;*.wav;*.m4a;*.flac;*.ogg;*.aac;*.wma|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        string mediaPath = dialog.FileName;
        string folder = Path.GetDirectoryName(mediaPath)!;
        string baseName = Path.GetFileNameWithoutExtension(mediaPath);
        var request = shell.CreateRequest(mode, new[] { new AudioTrack(null, mediaPath) }, folder, baseName);
        await shell.StartJobAsync(request);
    }
}
