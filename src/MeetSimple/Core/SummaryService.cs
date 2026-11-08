namespace MeetSimple.Core;

/// <summary>Map-reduce summary: condense each ~10-minute chunk, then merge the notes into one final summary.</summary>
public static class SummaryService
{
    private const int MaxWordsBeforeFinalMerge = 3000;

    private const string ChunkPrompt = """
        You are condensing one part ({0}) of a recording transcript. Lines may start with "You:" or "Others:" to show who spoke.
        Extract only the important information: facts, decisions, numbers, dates, names, tasks, problems and conclusions.
        Drop greetings, small talk, filler, repetition and tangents. Write concise bullet points.
        Do not invent anything that is not in the text. If this part has nothing important, reply exactly: Nothing important.

        Transcript:
        {1}
        """;

    private const string MergePrompt = """
        Below are condensed notes from consecutive parts of one recording.
        Merge them into a single shorter set of bullet-point notes. Keep every important fact, decision, number, name and task.
        Remove duplicates and filler. Do not invent anything.

        Notes:
        {0}
        """;

    private const string FinalPrompt = """
        Below are condensed notes from consecutive parts of one recording.
        Combine them into one final summary containing only the important information. Remove duplicates and filler.
        Do not invent anything. Use exactly this structure:

        ## Overview
        (2-4 sentences)

        ## Key points
        (bullets, grouped by topic when helpful)

        ## Decisions
        (bullets, or "None")

        ## Action items
        (bullets with owner and deadline if mentioned, or "None")

        ## Open questions
        (bullets, or "None")

        Notes:
        {0}
        """;

    public static async Task<string> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> segments, string model, IProgress<ProgressInfo> progress,
        CancellationToken cancellationToken, Func<string, string, CancellationToken, Task<string>>? generate = null)
    {
        generate ??= OllamaClient.GenerateAsync;

        var chunks = TextProcessing.BuildChunks(segments);
        if (chunks.Count == 0)
        {
            return "The transcript is empty, so there is nothing to summarize.";
        }

        var notes = new List<string>();
        for (int index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (timeRange, text) = chunks[index];
            progress.Report(new ProgressInfo((double)index / (chunks.Count + 1),
                $"Reading part {index + 1} of {chunks.Count} ({timeRange})..."));

            string chunkNotes = await generate(model, string.Format(ChunkPrompt, timeRange, text), cancellationToken);
            if (chunkNotes.Trim().TrimEnd('.').ToLowerInvariant() != "nothing important")
            {
                notes.Add($"[{timeRange}]\n{chunkNotes}");
            }
        }

        if (notes.Count == 0)
        {
            return "No important information was found in the recording.";
        }

        while (notes.Count > 1 && WordCount(notes) > MaxWordsBeforeFinalMerge)
        {
            progress.Report(new ProgressInfo(null, "Condensing notes..."));
            var merged = new List<string>();
            for (int start = 0; start < notes.Count; start += 3)
            {
                string group = string.Join("\n\n", notes.Skip(start).Take(3));
                merged.Add(await generate(model, string.Format(MergePrompt, group), cancellationToken));
            }
            notes = merged;
        }

        progress.Report(new ProgressInfo((double)chunks.Count / (chunks.Count + 1), "Writing the final summary..."));
        return await generate(model, string.Format(FinalPrompt, string.Join("\n\n", notes)), cancellationToken);
    }

    private static int WordCount(IEnumerable<string> notes) =>
        notes.Sum(note => note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
}
