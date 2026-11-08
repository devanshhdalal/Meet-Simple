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
