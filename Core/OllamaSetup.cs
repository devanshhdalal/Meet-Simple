using System.Diagnostics;

namespace MeetSimple.Core;

/// <summary>
/// Installs and starts Ollama on demand - only when the user chooses to summarize.
/// The installer and the program are only run if they carry a valid signature from Ollama Inc.
/// </summary>
public static class OllamaSetup
{
    private const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    private const string ExpectedPublisher = "Ollama Inc.";

    /// <summary>Only the standard install locations are used - never the PATH, which could point at a writable folder.</summary>
    public static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Makes sure the Ollama server is running. Returns true if it already was.</summary>
    public static async Task<bool> EnsureReadyAsync(IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        if (await OllamaClient.TryListModelsAsync(cancellationToken) != null) return true;

        string? executable = FindExecutable();
        if (executable == null)
        {
            executable = await InstallAsync(progress, cancellationToken);
        }

        progress.Report(new ProgressInfo(0.9, "Starting..."));
        await StartServerAsync(executable, cancellationToken);
        return false;
    }

    private static void RequireTrustedSignature(string filePath, string description)
    {
        if (!AuthenticodeVerifier.IsSignedBy(filePath, ExpectedPublisher, out string reason))
        {
            throw new InvalidOperationException(
                $"For your safety, {description} was not run because {reason}. " +
                "You can install Ollama yourself from https://ollama.com and try again.");
        }
    }

    private static async Task<string> InstallAsync(IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        string installerFolder = Path.Combine(Path.GetTempPath(), "MeetSimple", "ollama-" + Guid.NewGuid().ToString("N"));
        string installerPath = Path.Combine(installerFolder, "OllamaSetup.exe");
        try
        {
            var downloadProgress = new SyncProgress<ProgressInfo>(info =>
                progress.Report(new ProgressInfo(info.Fraction * 0.8, info.Detail)));
            await Downloader.DownloadAsync(InstallerUrl, installerPath, DownloadPolicies.OllamaInstaller, downloadProgress, cancellationToken);

            progress.Report(new ProgressInfo(0.8, "Checking the installer's signature..."));
            RequireTrustedSignature(installerPath, "the downloaded Ollama installer");

            progress.Report(new ProgressInfo(0.82, "Installing... this can take a minute"));
            using var installer = Process.Start(new ProcessStartInfo(installerPath, "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("The Ollama installer could not be started.");
            await installer.WaitForExitAsync(cancellationToken);
            if (installer.ExitCode != 0)
            {
                throw new InvalidOperationException($"The Ollama installer stopped with code {installer.ExitCode}.");
            }
        }
        finally
        {
            try { Directory.Delete(installerFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        for (int attempt = 0; attempt < 30; attempt++)
        {
            string? executable = FindExecutable();
            if (executable != null) return executable;
            await Task.Delay(1000, cancellationToken);
        }
        throw new InvalidOperationException("Ollama finished installing but could not be found.");
    }

    private static async Task StartServerAsync(string executable, CancellationToken cancellationToken)
    {
        if (await OllamaClient.TryListModelsAsync(cancellationToken) != null) return;

        RequireTrustedSignature(executable, "the Ollama program");
        Process.Start(new ProcessStartInfo(executable, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (await OllamaClient.TryListModelsAsync(cancellationToken) != null) return;
            await Task.Delay(1000, cancellationToken);
        }
        throw new InvalidOperationException(
            "Ollama is installed but did not start. Try opening Ollama from the Start menu, then retry.");
    }
}
