using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MeetSimple.Core;

/// <summary>Talks to the local Ollama server (http://localhost:11434). Nothing here leaves the computer.</summary>
public static class OllamaClient
{
    public const string BaseUrl = "http://localhost:11434";
    // Talks to localhost only, so a system-wide proxy must never be involved.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static void RequireValidModelName(string model)
    {
        if (!InputValidation.IsValidModelName(model))
        {
            throw new ArgumentException("That summary model name is not valid.");
        }
    }

    /// <summary>Returns installed model names, or null if the Ollama server is not reachable.</summary>
    public static async Task<List<string>?> TryListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await Http.GetAsync($"{BaseUrl}/api/tags", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));

            var models = new List<string>();
            if (document.RootElement.TryGetProperty("models", out var modelArray))
            {
                foreach (var model in modelArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameElement) && nameElement.GetString() is { } name)
                    {
                        models.Add(name);
                    }
                }
            }
            return models;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsInstalled(IEnumerable<string> installedModels, string model)
    {
        var installed = installedModels.ToList();
        return installed.Contains(model) || installed.Contains(model + ":latest");
    }

    /// <summary>Where Ollama keeps models: OLLAMA_MODELS if set, otherwise %USERPROFILE%\.ollama\models.</summary>
    public static string ModelsFolder
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            return !string.IsNullOrWhiteSpace(custom)
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
        }
    }

    /// <summary>
    /// Whether a model is already downloaded, answered from Ollama's manifest files, so it works while the
    /// server is not running yet ("llama3.1:8b" lives at manifests\registry.ollama.ai\library\llama3.1\8b).
    /// </summary>
    public static bool IsInstalledOnDisk(string model)
    {
        if (!InputValidation.IsValidModelName(model)) return false;
        string name = model;
        string tag = "latest";
        int colonIndex = model.LastIndexOf(':');
        if (colonIndex > model.LastIndexOf('/'))
        {
            name = model[..colonIndex];
            tag = model[(colonIndex + 1)..];
        }
        string[] nameParts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] location = nameParts.Length switch
        {
            1 => new[] { "registry.ollama.ai", "library", nameParts[0] },
            2 => new[] { "registry.ollama.ai", nameParts[0], nameParts[1] },
            _ => nameParts,
        };
        try
        {
            string manifestPath = Path.Combine(new[] { ModelsFolder, "manifests" }.Concat(location).Append(tag).ToArray());
            return File.Exists(manifestPath);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Loading an 8B model from a slow disk and reading a long prompt on a processor can take minutes before the first word.
    private static readonly TimeSpan FirstWordTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NextWordTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PullFirstUpdateTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PullNextUpdateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Streams the answer word by word, so a stalled AI is noticed in seconds instead of never.</summary>
    public static async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        RequireValidModelName(model);
        string payload = JsonSerializer.Serialize(new
        {
            model,
            prompt,
            stream = true,
            options = new { num_ctx = 8192, temperature = 0.2 },
        });

        using var watchdog = new IdleWatchdog(FirstWordTimeout, cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/generate")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The summary AI returned an error ({(int)response.StatusCode}). If the model is missing, run: ollama pull {model}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token);
            using var reader = new StreamReader(stream);
            var answer = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(watchdog.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var update = JsonDocument.Parse(line);
                var root = update.RootElement;
                if (root.TryGetProperty("error", out var errorElement))
                {
                    throw new InvalidOperationException("The summary AI reported a problem: " + errorElement.GetString());
                }
                if (root.TryGetProperty("response", out var pieceElement))
                {
                    answer.Append(pieceElement.GetString());
                }
                watchdog.Kick(NextWordTimeout);
                if (root.TryGetProperty("done", out var doneElement) && doneElement.ValueKind == JsonValueKind.True) break;
            }
            return answer.ToString().Trim();
        }
        catch (OperationCanceledException) when (watchdog.HasFired)
        {
            throw new TimeoutException(
                "The summary AI stopped responding. Your computer may be low on memory; try the lighter summary AI in Settings.");
        }
    }

    public static async Task PullModelAsync(string model, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        RequireValidModelName(model);
        string payload = JsonSerializer.Serialize(new { name = model, stream = true });
        using var watchdog = new IdleWatchdog(PullFirstUpdateTimeout, cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/pull")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token);
            using var reader = new StreamReader(stream);
            var layerTotals = new Dictionary<string, long>();
            var layerCompleted = new Dictionary<string, long>();
            bool diskSpaceChecked = false;

            string? line;
            while ((line = await reader.ReadLineAsync(watchdog.Token)) != null)
            {
                watchdog.Kick(PullNextUpdateTimeout);
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var update = JsonDocument.Parse(line);
                var root = update.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    throw new InvalidOperationException($"Could not download '{model}': {errorElement.GetString()}");
                }

                if (root.TryGetProperty("digest", out var digestElement)
                    && root.TryGetProperty("total", out var totalElement)
                    && digestElement.GetString() is { } digest)
                {
                    layerTotals[digest] = totalElement.GetInt64();
                    layerCompleted[digest] = root.TryGetProperty("completed", out var completedElement)
                        ? completedElement.GetInt64() : 0;
                }

                long totalBytes = layerTotals.Values.Sum();
                if (totalBytes > 0)
                {
                    long doneBytes = layerCompleted.Values.Sum();
                    if (!diskSpaceChecked)
                    {
                        // Fail in seconds with a clear message, not after gigabytes, when the drive is too full.
                        AppHealth.EnsureFreeSpace(ModelsFolder, totalBytes - doneBytes + 512L * 1024 * 1024);
                        diskSpaceChecked = true;
                    }
                    progress.Report(new ProgressInfo(Math.Min(1.0, (double)doneBytes / totalBytes),
                        $"Downloading... {doneBytes / 1e9:0.0} of {totalBytes / 1e9:0.0} GB"));
                }
                else
                {
                    string status = root.TryGetProperty("status", out var statusElement)
                        ? statusElement.GetString() ?? "Preparing..." : "Preparing...";
                    progress.Report(new ProgressInfo(null, status));
                }
            }
        }
        catch (OperationCanceledException) when (watchdog.HasFired)
        {
            throw new TimeoutException("The summary AI download stopped making progress. Check your internet connection and try again.");
        }
    }
}
