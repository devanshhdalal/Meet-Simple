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

    /// <summary>
    /// An echo is the far side leaking from the speakers into the microphone: the same words at the same moment.
    /// Long passages are matched loosely (60% shared words, 40% time overlap). Short replies such as "okay" or
    /// "yes, thanks" are often said by both sides, so they only count as an echo when the words are identical and
    /// they start within half a second of each other, which is how a speaker leak lines up and a real reply does not.
    /// </summary>
    private static bool IsEcho(TranscriptSegment microphoneSegment, List<TranscriptSegment> otherSegments)
    {
        const int ShortReplyWords = 3;
        double duration = Math.Max((microphoneSegment.End - microphoneSegment.Start).TotalSeconds, 0.1);
        var microphoneWords = WordSet(microphoneSegment.Text);
        if (microphoneWords.Count == 0) return false;
        bool isShortReply = microphoneWords.Count < ShortReplyWords;

        foreach (var other in otherSegments)
        {
            double overlap = (Min(microphoneSegment.End, other.End) - Max(microphoneSegment.Start, other.Start)).TotalSeconds;
            var otherWords = WordSet(other.Text);
            int shared = microphoneWords.Count(word => otherWords.Contains(word));
            int union = microphoneWords.Count + otherWords.Count - shared;
            if (union == 0) continue;
            double similarity = (double)shared / union;

            if (isShortReply)
            {
                double startGap = Math.Abs((microphoneSegment.Start - other.Start).TotalSeconds);
                if (similarity >= 1.0 && startGap <= 0.5 && overlap >= duration * 0.8) return true;
            }
            else if (overlap >= duration * 0.4 && similarity >= 0.6)
            {
                return true;
            }
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

    /// <summary>The filler and stutter rules are English ("um", "er", "uh"); in German "er" means "he" and in Portuguese "um" means "a".</summary>
    public static bool IsEnglish(TranscriptSegment segment) => segment.Language is "en" or "english";

    /// <summary>
    /// A language-neutral estimate of how many AI tokens a text uses. Counting spaces fails for Chinese, Japanese and
    /// Korean (no spaces, about one token per character); for other scripts about four characters make a token.
    /// It errs on the high side, which keeps every chunk inside the summary AI's context window.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        int wideCharacters = 0;
        int otherCharacters = 0;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character)) continue;
            if (IsWideScript(character)) wideCharacters++; else otherCharacters++;
        }
        return wideCharacters + (otherCharacters + 3) / 4;
    }

    private static bool IsWideScript(char character) =>
        character is >= '\u3040' and <= '\u30FF'   // Hiragana, Katakana
            or >= '\u3400' and <= '\u9FFF'         // CJK ideographs
            or >= '\uAC00' and <= '\uD7AF'         // Hangul syllables
            or >= '\uF900' and <= '\uFAFF';        // CJK compatibility ideographs

    /// <summary>Groups transcript lines into chunks of roughly maxTokens estimated tokens for the summarizer.</summary>
    public static List<(string TimeRange, string Text)> BuildChunks(IReadOnlyList<TranscriptSegment> segments, int maxTokens = 2000)
    {
        var chunks = new List<(string, string)>();
        var lines = new List<string>();
        int tokenCount = 0;
        TimeSpan chunkStart = TimeSpan.Zero;
        TimeSpan chunkEnd = TimeSpan.Zero;

        void Flush()
        {
            if (lines.Count > 0)
            {
                chunks.Add(($"{FormatTimestamp(chunkStart)} - {FormatTimestamp(chunkEnd)}", string.Join("\n", lines)));
            }
            lines.Clear();
            tokenCount = 0;
        }

        foreach (var segment in segments)
        {
            string cleaned = IsEnglish(segment) ? CleanFiller(segment.Text) : segment.Text.Trim();
            if (cleaned.Length == 0) continue;
            if (lines.Count == 0) chunkStart = segment.Start;

            lines.Add(segment.Speaker != null ? $"{segment.Speaker}: {cleaned}" : cleaned);
            tokenCount += EstimateTokens(cleaned);
            chunkEnd = segment.End;
            if (tokenCount >= maxTokens) Flush();
        }
        Flush();
        return chunks;
    }
}
