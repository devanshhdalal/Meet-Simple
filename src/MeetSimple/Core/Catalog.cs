namespace MeetSimple.Core;

public static class Catalog
{
    public const string DefaultSpeechModel = "large-v3-turbo-q5_0";

    public static readonly (string Id, string Label)[] SpeechModels =
    {
        ("tiny", "Tiny - fastest, least accurate (75 MB)"),
        ("base", "Base - fast (142 MB)"),
        ("small", "Small - quick, decent accuracy (466 MB)"),
        ("medium", "Medium - accurate, slow (1.5 GB)"),
        ("large-v3-turbo-q5_0", "Turbo - very accurate, recommended (574 MB)"),
        ("large-v3-turbo", "Turbo full precision - very accurate (1.6 GB)"),
        ("large-v3", "Large - most accurate, slowest (3 GB)"),
    };

    public static readonly (string Id, string Label)[] SummaryModels =
    {
        ("llama3.1:8b", "Llama 3.1 8B - best quality (about 5 GB)"),
        ("llama3.2:3b", "Llama 3.2 3B - faster and lighter (about 2 GB)"),
    };

    /// <summary>
    /// Every language the Whisper models know, by the exact code whisper.cpp accepts. Region codes such as
    /// "pt-BR" are not understood by the model, which is why this is a fixed list rather than free text.
    /// "" means detect automatically.
    /// </summary>
    public static readonly (string Id, string Label)[] Languages =
    {
        ("", "Detect automatically"),
        ("en", "English"),
        ("af", "Afrikaans"),
        ("sq", "Albanian"),
        ("am", "Amharic"),
        ("ar", "Arabic"),
        ("hy", "Armenian"),
        ("as", "Assamese"),
        ("az", "Azerbaijani"),
        ("ba", "Bashkir"),
        ("eu", "Basque"),
        ("be", "Belarusian"),
        ("bn", "Bengali"),
        ("bs", "Bosnian"),
        ("br", "Breton"),
        ("bg", "Bulgarian"),
        ("yue", "Cantonese"),
        ("ca", "Catalan"),
        ("zh", "Chinese"),
        ("hr", "Croatian"),
        ("cs", "Czech"),
        ("da", "Danish"),
        ("nl", "Dutch"),
        ("et", "Estonian"),
        ("fo", "Faroese"),
        ("fi", "Finnish"),
        ("fr", "French"),
        ("gl", "Galician"),
        ("ka", "Georgian"),
        ("de", "German"),
        ("el", "Greek"),
        ("gu", "Gujarati"),
        ("ht", "Haitian Creole"),
        ("ha", "Hausa"),
        ("haw", "Hawaiian"),
        ("he", "Hebrew"),
        ("hi", "Hindi"),
        ("hu", "Hungarian"),
        ("is", "Icelandic"),
        ("id", "Indonesian"),
        ("it", "Italian"),
        ("ja", "Japanese"),
        ("jw", "Javanese"),
        ("kn", "Kannada"),
        ("kk", "Kazakh"),
        ("km", "Khmer"),
        ("ko", "Korean"),
        ("lo", "Lao"),
        ("la", "Latin"),
        ("lv", "Latvian"),
        ("ln", "Lingala"),
        ("lt", "Lithuanian"),
        ("lb", "Luxembourgish"),
        ("mk", "Macedonian"),
        ("mg", "Malagasy"),
        ("ms", "Malay"),
        ("ml", "Malayalam"),
        ("mt", "Maltese"),
        ("mi", "Maori"),
        ("mr", "Marathi"),
        ("mn", "Mongolian"),
        ("my", "Myanmar"),
        ("ne", "Nepali"),
        ("no", "Norwegian"),
        ("nn", "Nynorsk"),
        ("oc", "Occitan"),
        ("ps", "Pashto"),
        ("fa", "Persian"),
        ("pl", "Polish"),
        ("pt", "Portuguese"),
        ("pa", "Punjabi"),
        ("ro", "Romanian"),
        ("ru", "Russian"),
        ("sa", "Sanskrit"),
        ("sr", "Serbian"),
        ("sn", "Shona"),
        ("sd", "Sindhi"),
        ("si", "Sinhala"),
        ("sk", "Slovak"),
        ("sl", "Slovenian"),
        ("so", "Somali"),
        ("es", "Spanish"),
        ("su", "Sundanese"),
        ("sw", "Swahili"),
        ("sv", "Swedish"),
        ("tl", "Tagalog"),
        ("tg", "Tajik"),
        ("ta", "Tamil"),
        ("tt", "Tatar"),
        ("te", "Telugu"),
        ("th", "Thai"),
        ("bo", "Tibetan"),
        ("tr", "Turkish"),
        ("tk", "Turkmen"),
        ("uk", "Ukrainian"),
        ("ur", "Urdu"),
        ("uz", "Uzbek"),
        ("vi", "Vietnamese"),
        ("cy", "Welsh"),
        ("yi", "Yiddish"),
        ("yo", "Yoruba"),
    };

    private static readonly System.Buffers.SearchValues<char> LanguageRegionSeparators = System.Buffers.SearchValues.Create("-_");

    public static bool IsKnownLanguage(string? code) => Languages.Any(language => language.Id == (code ?? ""));

    /// <summary>Turns older free-text settings ("EN", "pt-BR", "zh_CN") into a supported code, or "" (detect).</summary>
    public static string NormalizeLanguage(string? code)
    {
        string cleaned = (code ?? "").Trim().ToLowerInvariant();
        if (IsKnownLanguage(cleaned)) return cleaned;
        int separatorIndex = cleaned.AsSpan().IndexOfAny(LanguageRegionSeparators);
        if (separatorIndex > 0 && IsKnownLanguage(cleaned[..separatorIndex])) return cleaned[..separatorIndex];
        return "";
    }

    public static string LabelFor((string Id, string Label)[] choices, string id)
    {
        foreach (var choice in choices)
        {
            if (choice.Id == id) return choice.Label;
        }
        return id;
    }

    public static string IdFor((string Id, string Label)[] choices, string label, string fallback)
    {
        foreach (var choice in choices)
        {
            if (choice.Label == label) return choice.Id;
        }
        return string.IsNullOrWhiteSpace(label) ? fallback : label;
    }
}
