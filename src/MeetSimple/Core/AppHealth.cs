using System.Text;

namespace MeetSimple.Core;

/// <summary>Thrown before a recording or download starts when the drive cannot hold it.</summary>
public sealed class NotEnoughDiskSpaceException : Exception
{
    public NotEnoughDiskSpaceException(string message) : base(message)
    {
    }
}

/// <summary>
/// Small safety nets that keep the app dependable: an error log users can attach to bug reports, a marker that
/// detects a graphics-card crash, free-disk-space checks, collision-free names and clean-up of leftover temp files.
/// </summary>
public static class AppHealth
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object LogGate = new();

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetSimple");

    public static string LogPath => Path.Combine(DataFolder, "logs", "meet-simple.log");

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "MeetSimple");

    private static string GpuMarkerPath => Path.Combine(DataFolder, "gpu-attempt.marker");

    // ----- error log (scrubbed of user names and paths by Redaction) -----

    public static void LogError(Exception? error, string context)
    {
        if (error == null) return;
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var logFile = new FileInfo(LogPath);
                if (logFile.Exists && logFile.Length > MaxLogBytes)
                {
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                }
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}"
                               + Redaction.Scrub(error.ToString()) + Environment.NewLine + Environment.NewLine;
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Logging must never cause a second failure.
        }
    }

    // ----- graphics-card crash detection -----
    // A crash inside the native speech engine ends the process without any exception to catch. So a marker file is
    // written before transcribing and removed afterwards; if it is still there at the next start, the last run died.

    public static void MarkGpuAttempt()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(GpuMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    public static void ClearGpuAttempt()
    {
        try
        {
            File.Delete(GpuMarkerPath);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>True (once) when the previous run ended while the marker was still present.</summary>
    public static bool ConsumeCrashedGpuAttempt()
    {
        try
        {
            if (!File.Exists(GpuMarkerPath)) return false;
            File.Delete(GpuMarkerPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ----- disk space and file names -----

    public static void EnsureFreeSpace(string pathOnDrive, long requiredBytes)
    {
        long availableBytes;
        string driveRoot;
        try
        {
            driveRoot = Path.GetPathRoot(Path.GetFullPath(pathOnDrive)) ?? "";
            if (driveRoot.Length == 0) return;
            availableBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return; // Cannot tell (for example a network share); the operation itself will report a real problem.
        }

        if (availableBytes < requiredBytes)
        {
            throw new NotEnoughDiskSpaceException(
                $"Not enough free disk space on {driveRoot}: about {requiredBytes / 1e9:0.0} GB is needed and {availableBytes / 1e9:0.0} GB is free.");
        }
    }

    /// <summary>A base name whose transcript and summary files do not exist yet, so earlier results are never overwritten.</summary>
    public static string UniqueBaseName(string folder, string baseName)
    {
        string candidate = baseName;
        for (int number = 2;
             File.Exists(Path.Combine(folder, candidate + "_transcript.txt"))
             || File.Exists(Path.Combine(folder, candidate + "_summary.txt"));
             number++)
        {
            candidate = $"{baseName} ({number})";
        }
        return candidate;
    }

    public static string UniqueFolder(string parentFolder, string folderName)
    {
        string candidate = folderName;
        for (int number = 2; Directory.Exists(Path.Combine(parentFolder, candidate)); number++)
        {
            candidate = $"{folderName} ({number})";
        }
        return Path.Combine(parentFolder, candidate);
    }

    // ----- leftovers -----

    /// <summary>Removes work folders (converted audio, installers) left behind by a crash or a killed process.</summary>
    public static void CleanOldTempFolders()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (string folder in Directory.EnumerateDirectories(TempRoot))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromHours(24))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // In use or protected; try again next start.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing to clean.
        }
    }
}
