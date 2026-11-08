using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace MeetSimple.Core;

public sealed record UnfinishedRecording(
    string Folder, JobMode Mode, DateTime StartedAt, IReadOnlyList<AudioTrack> Tracks, TimeSpan Duration);

/// <summary>
/// A meeting recording is never lost silently. Each recording folder gets a small marker while it has no transcript yet;
/// if the app is closed or crashes while recording, or the job never finishes, the next start finds the marker,
/// repairs the audio files and offers to transcribe them.
/// </summary>
public static class RecordingRecovery
{
    private const string MarkerName = "unfinished.json";
    private const long MaxMarkerBytes = 4 * 1024;
    private static readonly TimeSpan MinimumUsefulDuration = TimeSpan.FromSeconds(3);

    private sealed record Marker(JobMode Mode, DateTime StartedAt);

    /// <summary>File name of one part of a track: "microphone.wav", then "microphone (part 2).wav" and so on.</summary>
    public static string PartPath(string folder, string stem, int partNumber) =>
        Path.Combine(folder, partNumber == 1 ? stem + ".wav" : $"{stem} (part {partNumber}).wav");

    public static void MarkUnfinished(string folder, JobMode mode)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, MarkerName), JsonSerializer.Serialize(new Marker(mode, DateTime.Now)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Recovery is a safety net; recording still works without it.
        }
    }

    public static void Clear(string folder)
    {
        try
        {
            File.Delete(Path.Combine(folder, MarkerName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Asked again next time, which is harmless.
        }
    }

    /// <summary>
    /// The newest recording that still has no transcript, with its audio files repaired, or null.
    /// A folder that is being recorded into right now is skipped.
    /// </summary>
    public static UnfinishedRecording? FindLatest(string? folderBeingRecorded = null)
    {
        try
        {
            if (!Directory.Exists(AppSettings.RecordingsRoot)) return null;
            var folders = Directory.EnumerateDirectories(AppSettings.RecordingsRoot)
                .Where(folder => File.Exists(Path.Combine(folder, MarkerName)))
                .Where(folder => folderBeingRecorded == null
                                 || !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(folderBeingRecorded), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTimeUtc);
            foreach (string folder in folders)
            {
                var found = Inspect(folder);
                if (found != null) return found;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The recordings folder cannot be read right now; nothing to offer.
        }
        return null;
    }

    /// <summary>
    /// One recording folder (for example the one a "call recorded" notification points to). Only folders directly
    /// inside the Recordings folder that still carry the marker are accepted, so a crafted path cannot point elsewhere.
    /// </summary>
    public static UnfinishedRecording? Load(string folder)
    {
        try
        {
            string fullFolder = Path.GetFullPath(folder);
            string recordingsRoot = Path.GetFullPath(AppSettings.RecordingsRoot);
            bool isRecordingFolder = string.Equals(Path.GetDirectoryName(fullFolder), recordingsRoot, StringComparison.OrdinalIgnoreCase);
            if (!isRecordingFolder || !File.Exists(Path.Combine(fullFolder, MarkerName))) return null;
            return Inspect(fullFolder);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static UnfinishedRecording? Inspect(string folder)
    {
        string markerPath = Path.Combine(folder, MarkerName);
        Marker? marker;
        try
        {
            if (new FileInfo(markerPath).Length > MaxMarkerBytes) { Clear(folder); return null; }
            marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath));
        }
        catch (JsonException)
        {
            Clear(folder);
            return null;
        }
        if (marker == null || !Enum.IsDefined(marker.Mode)) { Clear(folder); return null; }

        var tracks = new List<AudioTrack>();
        AddTrack(tracks, folder, "You", "microphone");
        AddTrack(tracks, folder, "Others", "computer-audio");
        foreach (string path in tracks.SelectMany(track => track.SourcePaths))
        {
            WavRepair.TryRepair(path);
        }

        TimeSpan duration = tracks.Count == 0
            ? TimeSpan.Zero
            : tracks.Max(track => track.SourcePaths.Aggregate(TimeSpan.Zero, (total, path) => total + WavRepair.DurationOf(path)));
        if (duration < MinimumUsefulDuration)
        {
            Clear(folder);
            return null;
        }
        return new UnfinishedRecording(folder, marker.Mode, marker.StartedAt, tracks, duration);
    }

    private static void AddTrack(List<AudioTrack> tracks, string folder, string speaker, string stem)
    {
        var parts = new List<string>();
        for (int partNumber = 1; File.Exists(PartPath(folder, stem, partNumber)); partNumber++)
        {
            parts.Add(PartPath(folder, stem, partNumber));
        }
        if (parts.Count > 0) tracks.Add(new AudioTrack(speaker, parts));
    }
}

/// <summary>
/// A WAV file records its own length in its header, and a writer that never closed (crash, power cut) leaves that
/// length out of date. This rewrites the RIFF and data sizes from the real file length, so all audio on disk plays.
/// </summary>
public static class WavRepair
{
    public static void TryRepair(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            if (stream.Length < 44) return;
            if (ReadTag(reader) != "RIFF") return;
            reader.ReadUInt32();
            if (ReadTag(reader) != "WAVE") return;

            int blockAlign = 1;
            while (stream.Position + 8 <= stream.Length)
            {
                long chunkStart = stream.Position;
                string tag = ReadTag(reader);
                uint declaredSize = reader.ReadUInt32();
                long bodyStart = stream.Position;

                if (tag == "data")
                {
                    long available = stream.Length - bodyStart;
                    available -= available % blockAlign;
                    available = Math.Min(available, uint.MaxValue - bodyStart);
                    if (available == declaredSize) return;
                    stream.Position = chunkStart + 4;
                    writer.Write((uint)available);
                    stream.Position = 4;
                    writer.Write((uint)(bodyStart + available - 8));
                    return;
                }

                if (tag == "fmt " && declaredSize >= 14)
                {
                    stream.Position = bodyStart + 12;
                    blockAlign = Math.Max(1, (int)reader.ReadUInt16());
                }
                stream.Position = bodyStart + declaredSize + (declaredSize % 2);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leave the file as it is; reading it will report a clear problem if it is really damaged.
        }
    }

    public static TimeSpan DurationOf(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            return reader.TotalTime;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            return TimeSpan.Zero;
        }
    }

    private static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
