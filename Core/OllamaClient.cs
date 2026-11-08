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

    public static async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        RequireValidModelName(model);
        string payload = JsonSerializer.Serialize(new
        {
            model,
            prompt,
            stream = false,
            options = new { num_ctx = 8192, temperature = 0.2 },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync($"{BaseUrl}/api/generate", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The summary AI returned an error ({(int)response.StatusCode}). If the model is missing, run: ollama pull {model}");
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("response").GetString()?.Trim() ?? "";
    }

    public static async Task PullModelAsync(string model, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        RequireValidModelName(model);
        string payload = JsonSerializer.Serialize(new { name = model, stream = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/pull")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var layerTotals = new Dictionary<string, long>();
        var layerCompleted = new Dictionary<string, long>();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
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
}
