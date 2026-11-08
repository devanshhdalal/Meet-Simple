namespace MeetSimple.Core;

/// <summary>Removes personal details from error text before it is shown or copied (people paste these into public issues).</summary>
public static class Redaction
{
    public static string Scrub(string text)
    {
        text = ReplaceIgnoreCase(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        text = ReplaceIgnoreCase(text, Environment.UserName, "<user>");
        text = ReplaceIgnoreCase(text, Environment.MachineName, "<computer>");
        return text;
    }

    private static string ReplaceIgnoreCase(string text, string? value, string replacement) =>
        string.IsNullOrWhiteSpace(value) || value.Length < 3
            ? text
            : text.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);
}
