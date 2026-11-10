using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace MeetSimple.Core;

/// <summary>Downloads a verified release zip, extracts it, then hands off to a tiny script that
/// replaces files after this process exits (the running exe cannot overwrite itself).</summary>
public static class AppUpdater
{
    public static readonly DownloadPolicy ReleaseZipPolicy = new(
        new[] { "api.github.com", "github.com", "githubusercontent.com" },
        500L * 1024 * 1024);

    public static string PendingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetSimple", "pending-update");

    /// <summary>Folder that contains MeetSimple.exe for this run.</summary>
    public static string InstallDirectory
    {
        get
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                string? dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(dir)) return Path.GetFullPath(dir);
            }
            return Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    public static bool CanWriteInstallDirectory()
    {
        try
        {
            string probe = Path.Combine(InstallDirectory, $".meetsimple-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void OpenReleasePage(AppUpdateInfo update)
    {
        if (!UpdateChecker.IsAllowedReleasePage(update.ReleasePageUrl)) return;
        Process.Start(new ProcessStartInfo(update.ReleasePageUrl) { UseShellExecute = true });
    }

    /// <summary>Download, verify, extract. Throws on failure. Does not exit the app.</summary>
    public static async Task PrepareAsync(
        AppUpdateInfo update, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        if (!CanWriteInstallDirectory())
        {
            throw new InvalidOperationException(
                "Meet Simple cannot write to its install folder. Open the download page and update manually.");
        }

        string? expectedSha = await ResolveExpectedSha256Async(update, cancellationToken);
        if (string.IsNullOrEmpty(expectedSha))
        {
            throw new InvalidDataException(
                "This release has no checksum we can verify, so the update was not installed automatically.");
        }

        Directory.CreateDirectory(PendingRoot);
        string zipPath = Path.Combine(PendingRoot, UpdateChecker.ZipAssetName);
        string extractRoot = Path.Combine(PendingRoot, "extract");
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);

        string requiredSha = expectedSha;
        progress.Report(new ProgressInfo(0.02, "Downloading update..."));
        await Downloader.DownloadAsync(
            update.ZipDownloadUrl,
            zipPath,
            ReleaseZipPolicy,
            progress,
            cancellationToken,
            file => string.Equals(file.Sha256, requiredSha, StringComparison.OrdinalIgnoreCase)
                ? null
                : "The update failed its integrity check and was discarded.");

        progress.Report(new ProgressInfo(0.92, "Preparing files..."));
        string appFolder = ExtractZipSafely(zipPath, extractRoot);
        progress.Report(new ProgressInfo(1.0, "Ready to install"));
        // Stash path for ApplyAndExit.
        File.WriteAllText(Path.Combine(PendingRoot, "source.txt"), appFolder, Encoding.UTF8);
    }

    /// <summary>Writes apply.cmd, starts it, then the caller should shut down the app.</summary>
    public static void StartApplyAndExit()
    {
        string sourceFile = Path.Combine(PendingRoot, "source.txt");
        if (!File.Exists(sourceFile))
        {
            throw new InvalidOperationException("The update package is missing.");
        }

        string source = File.ReadAllText(sourceFile).Trim();
        string target = InstallDirectory;
        string exeName = "MeetSimple.exe";
        string applyPath = Path.Combine(PendingRoot, "apply.cmd");
        int pid = Environment.ProcessId;

        // Fixed script we own — never resolved from PATH.
        string script =
            $"""
            @echo off
            setlocal EnableExtensions
            set "PID={pid}"
            set "SOURCE={source}"
            set "TARGET={target}"
            set "EXE={exeName}"
            :wait
            tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            robocopy "%SOURCE%" "%TARGET%" /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np >nul
            start "" "%TARGET%\%EXE%"
            endlocal
            """;
        File.WriteAllText(applyPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Process.Start(new ProcessStartInfo
        {
            FileName = applyPath,
            UseShellExecute = true,
            WorkingDirectory = PendingRoot,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static async Task<string?> ResolveExpectedSha256Async(AppUpdateInfo update, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(update.Sha256DownloadUrl))
        {
            try
            {
                string checksumPath = Path.Combine(PendingRoot, UpdateChecker.Sha256AssetName);
                Directory.CreateDirectory(PendingRoot);
                // Quiet progress — the zip download is what the user sees.
                var quiet = new Progress<ProgressInfo>(_ => { });
                await Downloader.DownloadAsync(
                    update.Sha256DownloadUrl, checksumPath, ReleaseZipPolicy, quiet, cancellationToken);
                string text = await File.ReadAllTextAsync(checksumPath, cancellationToken);
                string? fromFile = UpdateChecker.FindSha256InText(text);
                if (fromFile != null) return fromFile;
            }
            catch (Exception)
            {
                // Fall through to release-body checksum.
            }
        }

        return update.Sha256FromBody;
    }

    /// <summary>Extracts the zip and returns the folder that contains MeetSimple.exe.</summary>
    private static string ExtractZipSafely(string zipPath, string extractRoot)
    {
        string rootFull = Path.GetFullPath(extractRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(extractRoot, relative));
            if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update archive had an unsafe path and was discarded.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        string? exe = Directory.EnumerateFiles(extractRoot, "MeetSimple.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (exe == null)
        {
            throw new InvalidDataException("The update archive did not contain MeetSimple.exe.");
        }

        return Path.GetDirectoryName(exe)!;
    }
}
