using System.Text.Json;

namespace MeetSimple.Core;

public sealed class AppSettings
{
    private const int CurrentSettingsVersion = 2;

    /// <summary>Lets newer builds upgrade defaults that older builds saved to disk.</summary>
    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    public bool TutorialCompleted { get; set; }
    public string SpeechModel { get; set; } = Catalog.DefaultSpeechModel;

    /// <summary>true = beam search (more accurate, slower); false = greedy (faster).</summary>
    public bool BestAccuracy { get; set; } = true;

    /// <summary>Names and jargon the speech model should spell correctly, e.g. "Anmol, Kubernetes, OKRs".</summary>
    public string Glossary { get; set; } = "";

    /// <summary>false = processor only. Switched off automatically if the graphics card crashed during a transcription.</summary>
    public bool UseGraphicsCard { get; set; } = true;

    /// <summary>Ask to record when another app starts a call. While on, closing the window keeps Meet Simple in the tray.</summary>
    public bool WatchForCalls { get; set; } = true;

    /// <summary>Open quietly in the tray when the user signs in to Windows. Off unless the user turns it on.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>The one-time "still running in the tray" notice has been shown.</summary>
    public bool TrayNoticeShown { get; set; }
    public string SummaryModel { get; set; } = "llama3.1:8b";
    public string Language { get; set; } = "";

    /// <summary>Microphone and output recorded by "Record a meeting" ("" = the Windows default). Set from the record screen.</summary>
    public string RecordMicrophoneId { get; set; } = "";
    public string RecordComputerAudioId { get; set; } = "";

    /// <summary>"system" (follow Windows, the default), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>The colour space (see SpaceManager): violet, ocean, forest, sunset or graphite.</summary>
    public string Space { get; set; } = Theming.SpaceManager.DefaultId;

    /// <summary>When on (the default), ask if GitHub has a newer Meet Simple release.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Last successful contact with the GitHub releases API (UTC).</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Release tag we last snoozed (with or without a leading v).</summary>
    public string SnoozedUpdateVersion { get; set; } = "";

    /// <summary>Do not ask again about <see cref="SnoozedUpdateVersion"/> until this UTC time.</summary>
    public DateTime? SnoozeUntilUtc { get; set; }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetSimple", "settings.json");

    private const long MaxSettingsFileBytes = 64 * 1024;

    public static AppSettings Current { get; } = Load();

    /// <summary>The settings file is user-editable text, so anything unexpected falls back to a safe default.</summary>
    public void Sanitize()
    {
        if (!InputValidation.IsKnownSpeechModel(SpeechModel)) SpeechModel = Catalog.DefaultSpeechModel;
        Glossary = InputValidation.CleanGlossary(Glossary);
        if (!InputValidation.IsValidModelName(SummaryModel)) SummaryModel = "llama3.1:8b";
        Language = Catalog.NormalizeLanguage(Language);
        if (Theme != "light" && Theme != "dark") Theme = "system";
        if (!Theming.SpaceManager.IsKnown(Space)) Space = Theming.SpaceManager.DefaultId;
        RecordMicrophoneId = CleanDeviceId(RecordMicrophoneId);
        RecordComputerAudioId = CleanDeviceId(RecordComputerAudioId);
    }

    /// <summary>Windows endpoint ids look like "{0.0.1.00000000}.{guid}"; anything else is dropped.</summary>
    private static string CleanDeviceId(string? deviceId) =>
        deviceId is { Length: > 0 and <= 200 } && deviceId.StartsWith("{0.0.", StringComparison.Ordinal) ? deviceId : "";

    private static AppSettings Load()
    {
        try
        {
            var settingsFile = new FileInfo(SettingsPath);
            if (settingsFile.Exists && settingsFile.Length <= MaxSettingsFileBytes)
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                if (loaded.SettingsVersion < CurrentSettingsVersion)
                {
                    // Version 1 saved "small" as the default; move people who never changed it to the better default.
                    if (loaded.SpeechModel == "small") loaded.SpeechModel = Catalog.DefaultSpeechModel;
                    loaded.SettingsVersion = CurrentSettingsVersion;
                }
                loaded.Sanitize();
                return loaded;
            }
        }
        catch (Exception)
        {
            // A damaged settings file should never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Sanitize();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Write to a temporary file first so a crash or power cut can never leave a half-written settings file.
            string temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception)
        {
            // Settings are a convenience; ignore write problems.
        }
    }

    public static string RecordingsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meet Simple");
}
