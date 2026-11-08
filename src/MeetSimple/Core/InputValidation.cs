using System.Text.RegularExpressions;

namespace MeetSimple.Core;

/// <summary>Strict checks for every value that ends up in a URL, a file name, a command or a prompt.</summary>
public static class InputValidation
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // e.g. "llama3.1:8b", "library/qwen2.5:7b" - no spaces, quotes, dots-only segments or control characters.
    private static readonly Regex ModelNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,80}(:[A-Za-z0-9._\-]{1,40})?$", RegexOptions.Compiled, MatchTimeout);

    // e.g. "en", "pt", "zh-CN"
    private static readonly Regex LanguagePattern = new(
        @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$", RegexOptions.Compiled, MatchTimeout);

    public static bool IsValidModelName(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Contains("..") && ModelNamePattern.IsMatch(value);

    /// <summary>Keeps only characters that are safe in a file name (no separators, no reserved names).</summary>
    public static string SafeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string((name ?? "").Where(character => !invalid.Contains(character)).ToArray()).Trim(' ', '.');
        if (cleaned.Length > 100) cleaned = cleaned[..100].TrimEnd(' ', '.');
        return cleaned.Length == 0 ? "meeting" : cleaned;
    }

    public static bool IsValidLanguage(string? value) =>
        string.IsNullOrEmpty(value) || LanguagePattern.IsMatch(value);

    public const int MaxGlossaryLength = 400;

    /// <summary>
    /// The glossary is typed by the user and fed to the speech model as a hint, so strip control characters,
    /// collapse whitespace and keep it short (Whisper only reads about 220 tokens of prompt).
    /// </summary>
    public static string CleanGlossary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var visible = new System.Text.StringBuilder(value.Length);
        foreach (char character in value)
        {
            visible.Append(char.IsControl(character) ? ' ' : character);
        }
        string collapsed = string.Join(' ', visible.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxGlossaryLength ? collapsed : collapsed[..MaxGlossaryLength].TrimEnd();
    }

    public static bool IsKnownSpeechModel(string? value) =>
        Catalog.SpeechModels.Any(choice => choice.Id == value);
}
