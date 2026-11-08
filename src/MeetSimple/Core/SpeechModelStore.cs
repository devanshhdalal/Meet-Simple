namespace MeetSimple.Core;

/// <summary>Speech-recognition models are downloaded the first time they are needed, verified, then reused.</summary>
public static class SpeechModelStore
{
    public static string ModelsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetSimple", "models");

    public static string PathFor(string modelId) => Path.Combine(ModelsFolder, $"ggml-{modelId}.bin");

    /// <summary>Silero VAD in whisper.cpp's format (under 1 MB): finds where people speak (see VoiceDetector).</summary>
    public static string VoiceDetectorPath => Path.Combine(ModelsFolder, "ggml-silero-v5.1.2.bin");

    private const string VoiceDetectorUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin";

    /// <summary>
    /// Makes sure the voice detector is on disk; returns its path, or null if it could not be fetched (transcription
    /// then simply runs on the full recording, as before). Verified like the speech models.
    /// </summary>
    public static async Task<string?> EnsureVoiceDetectorAsync(IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        if (File.Exists(VoiceDetectorPath)) return VoiceDetectorPath;
        try
        {
            progress.Report(new ProgressInfo(null, "Getting the voice detector..."));
            await Downloader.DownloadAsync(VoiceDetectorUrl, VoiceDetectorPath, DownloadPolicies.HuggingFace, progress, cancellationToken, VerifyChecksum);
            return VoiceDetectorPath;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppHealth.LogError(error, "Voice detector download (transcribing without it)");
            return null;
        }
    }

    public static async Task<(string Path, bool AlreadyPresent)> EnsureAsync(
        string modelId, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        // The id becomes part of a URL and a file name, so only known models are accepted.
        if (!InputValidation.IsKnownSpeechModel(modelId))
        {
            throw new ArgumentException("Unknown speech model.");
        }

        string modelPath = PathFor(modelId);
        if (File.Exists(modelPath))
        {
            return (modelPath, true);
        }

        string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelId}.bin";
        progress.Report(new ProgressInfo(0.0, "Starting download..."));
        await Downloader.DownloadAsync(url, modelPath, DownloadPolicies.HuggingFace, progress, cancellationToken, VerifyChecksum);
        return (modelPath, false);
    }

    /// <summary>Models are parsed by native code, so a corrupted or swapped file is rejected instead of being loaded.</summary>
    private static string? VerifyChecksum(DownloadedFile file)
    {
        if (file.ServerSha256 == null)
        {
            return "The server did not provide a checksum, so the model could not be verified and was discarded.";
        }
        return string.Equals(file.Sha256, file.ServerSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "The downloaded model failed its integrity check and was discarded. Please try again.";
    }
}
