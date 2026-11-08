using System.Text;
using System.Text.RegularExpressions;

namespace MeetSimple.Core;

public static class TextProcessing
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex FillerPattern = new(
        @"\b(?:u+h+m*|u+m+|e+r+m*|h+m+|m+h+m+|a+h+)\b[,.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex RepeatedWordPattern = new(
        @"\b(\w+)(?:\s+\1\b)+", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex MultipleSpaces = new(@"\s{2,}", RegexOptions.Compiled, RegexTimeout);

    public static string FormatTimestamp(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    public static string CleanFiller(string text)
    {
        try
        {
            string withoutFillers = FillerPattern.Replace(text, "");
            string withoutStutters = RepeatedWordPattern.Replace(withoutFillers, "$1");
            return MultipleSpaces.Replace(withoutStutters, " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return text.Trim(); // Pathological input: keep the text as spoken rather than hang.
        }
    }

    /// <summary>Whisper emits markers such as [BLANK_AUDIO] or (music) for non-speech; they are not content.</summary>
    public static bool IsNoiseMarker(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        bool wrappedInBrackets = (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                                 || (trimmed.StartsWith('(') && trimmed.EndsWith(')'));
        return wrappedInBrackets || trimmed.All(character => !char.IsLetterOrDigit(character));
    }

    /// <summary>Whisper sometimes loops the same sentence over silence; drop the third and later identical repeats.</summary>
    public static List<TranscriptSegment> RemoveRepetitionLoops(IEnumerable<TranscriptSegment> segments)
    {
        var kept = new List<TranscriptSegment>();
        string previousText = "";
        int repeatCount = 0;
        foreach (var segment in segments)
        {
            string normalized = segment.Text.Trim().ToLowerInvariant();
            repeatCount = normalized == previousText ? repeatCount + 1 : 0;
            previousText = normalized;
            if (repeatCount < 2) kept.Add(segment);
        }
        return kept;
    }

    /// <summary>
    /// Combines tracks into one timeline. If the microphone picked up the other side from the speakers,
    /// the same words appear on both tracks at the same time; keep the computer-audio version.
    /// </summary>
    public static List<TranscriptSegment> MergeTracks(IEnumerable<IReadOnlyList<TranscriptSegment>> tracks)
    {
        var all = tracks.SelectMany(track => track).ToList();
        var others = all.Where(segment => segment.Speaker == "Others").ToList();
        var merged = all.Where(segment => segment.Speaker != "You" || !IsEcho(segment, others)).ToList();
        return merged.OrderBy(segment => segment.Start).ToList();
    }

    private static bool IsEcho(TranscriptSegment microphoneSegment, List<TranscriptSegment> otherSegments)
    {
        double duration = Math.Max((microphoneSegment.End - microphoneSegment.Start).TotalSeconds, 0.1);
        var microphoneWords = WordSet(microphoneSegment.Text);
        if (microphoneWords.Count == 0) return false;

        foreach (var other in otherSegments)
        {
            double overlap = (Min(microphoneSegment.End, other.End) - Max(microphoneSegment.Start, other.Start)).TotalSeconds;
            if (overlap < duration * 0.4) continue;

            var otherWords = WordSet(other.Text);
            int shared = microphoneWords.Count(word => otherWords.Contains(word));
            int union = microphoneWords.Count + otherWords.Count - shared;
            if (union > 0 && (double)shared / union >= 0.6) return true;
        }
        return false;
    }

    private static HashSet<string> WordSet(string text)
    {
        try
        {
            return WordPattern.Matches(text.ToLowerInvariant()).Select(match => match.Value).ToHashSet();
        }
        catch (RegexMatchTimeoutException)
        {
            return new HashSet<string>();
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    public static string RenderTranscript(IEnumerable<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            string speaker = segment.Speaker != null ? segment.Speaker + ": " : "";
            builder.AppendLine($"[{FormatTimestamp(segment.Start)}] {speaker}{segment.Text}");
        }
        return builder.ToString();
    }

    /// <summary>Groups cleaned transcript lines into chunks of roughly maxWords words for the summarizer.</summary>
    public static List<(string TimeRange, string Text)> BuildChunks(IReadOnlyList<TranscriptSegment> segments, int maxWords = 1500)
    {
        var chunks = new List<(string, string)>();
        var lines = new List<string>();
        int wordCount = 0;
        TimeSpan chunkStart = TimeSpan.Zero;
        TimeSpan chunkEnd = TimeSpan.Zero;

        void Flush()
        {
            if (lines.Count > 0)
            {
                chunks.Add(($"{FormatTimestamp(chunkStart)} - {FormatTimestamp(chunkEnd)}", string.Join("\n", lines)));
            }
            lines.Clear();
            wordCount = 0;
        }

        foreach (var segment in segments)
        {
            string cleaned = CleanFiller(segment.Text);
            if (cleaned.Length == 0) continue;
            if (lines.Count == 0) chunkStart = segment.Start;

            lines.Add(segment.Speaker != null ? $"{segment.Speaker}: {cleaned}" : cleaned);
            wordCount += cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            chunkEnd = segment.End;
            if (wordCount >= maxWords) Flush();
        }
        Flush();
        return chunks;
    }
}
