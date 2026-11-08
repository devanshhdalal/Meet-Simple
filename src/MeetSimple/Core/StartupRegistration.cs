using System.Security;
using Microsoft.Win32;

namespace MeetSimple.Core;

/// <summary>
/// "Start with Windows": the standard per-user Run entry (no administrator rights). Re-applied at every start, so it
/// follows the app if its folder is moved, and removed completely when switched off.
/// </summary>
public static class StartupRegistration
{
    public const string BackgroundArgument = "--background";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MeetSimple";

    public static void Apply(bool startWithWindows)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (startWithWindows && Environment.ProcessPath is { } executablePath)
            {
                string command = $"\"{executablePath}\" {BackgroundArgument}";
                if (!string.Equals(runKey.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                {
                    runKey.SetValue(ValueName, command);
                }
            }
            else if (runKey.GetValue(ValueName) != null)
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            AppHealth.LogError(error, "Start with Windows setting");
        }
    }
}
