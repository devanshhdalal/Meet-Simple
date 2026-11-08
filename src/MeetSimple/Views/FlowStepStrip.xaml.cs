using System.Windows.Controls;
using MeetSimple.Core;

namespace MeetSimple.Views;

/// <summary>Compact trail for wizard screens: mode · current step.</summary>
public partial class FlowStepStrip : UserControl
{
    public FlowStepStrip()
    {
        InitializeComponent();
    }

    public void Set(JobMode mode, string step)
    {
        ModeText.Text = mode == JobMode.TranscribeAndSummarize ? "Summarize" : "Transcribe";
        StepText.Text = step;
    }
}
