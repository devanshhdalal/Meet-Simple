using System.Net.Http;
using System.Security.Cryptography;

namespace MeetSimple.Core;

/// <summary>Which hosts a download may touch (including every redirect hop) and how large it may be.</summary>
public sealed record DownloadPolicy(string[] AllowedHostSuffixes, long MaxBytes);

/// <summary>SHA-256 of what was received, and the checksum the server advertised (Hugging Face's X-Linked-Etag), if any.</summary>
public sealed record DownloadedFile(string Sha256, string? ServerSha256);

public static class DownloadPolicies
{
    public static readonly DownloadPolicy HuggingFace = new(new[] { "huggingface.co", "hf.co" }, 4L * 1024 * 1024 * 1024);

    // ollama.com redirects to a GitHub release, which redirects to GitHub's asset storage.
    public static readonly DownloadPolicy OllamaInstaller = new(
        new[] { "ollama.com", "github.com", "githubusercontent.com" }, 5L * 1024 * 1024 * 1024);
}

/// <summary>
/// HTTPS-only downloads: redirects are followed by hand so every hop is checked against the allowlist,
/// size is capped, a SHA-256 is computed while streaming, and nothing is moved into place until it is verified.
/// </summary>
public static class Downloader
{
    private const int MaxRedirects = 5;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeetSimple/1.0");
        return client;
    }

    /// <param name="verify">Returns an error message to reject the file, or null to accept it. Runs before the file is moved into place.</param>
    public static async Task DownloadAsync(
        string url, string destinationPath, DownloadPolicy policy, IProgress<ProgressInfo> progress,
        CancellationToken cancellationToken, Func<DownloadedFile, string?>? verify = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string partialPath = destinationPath + ".part";
        try
        {
            var downloaded = await DownloadWithRetriesAsync(url, partialPath, policy, progress, cancellationToken);
            string? rejection = verify?.Invoke(downloaded);
            if (rejection != null)
            {
                throw new InvalidDataException(rejection);
            }
            File.Move(partialPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partialPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>A dropped connection on a multi-gigabyte download is routine, so brief network trouble is retried.</summary>
    private static async Task<DownloadedFile> DownloadWithRetriesAsync(
        string url, string partialPath, DownloadPolicy policy, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadToPartialFileAsync(url, partialPath, policy, progress, cancellationToken);
            }
            catch (Exception error) when (attempt < MaxAttempts && IsTransient(error, cancellationToken))
            {
                progress.Report(new ProgressInfo(null, $"Connection problem. Trying again ({attempt + 1} of {MaxAttempts})..."));
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        return error switch
        {
            HttpRequestException http => http.StatusCode is not { } status
                                         || (int)status >= 500
                                         || status is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests,
            IOException or TimeoutException => true,
            _ => false,
        };
    }

    private static async Task<int> ReadWithStallGuardAsync(
        Stream source, byte[] buffer, CancellationTokenSource stallGuard, CancellationToken cancellationToken)
    {
        stallGuard.CancelAfter(StallTimeout);
        try
        {
            return await source.ReadAsync(buffer, stallGuard.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The download stalled because no data arrived for a minute.");
        }
    }

    private static async Task<DownloadedFile> DownloadToPartialFileAsync(
        string url, string partialPath, DownloadPolicy policy, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
    {
        var (response, serverSha256) = await OpenAsync(url, policy, cancellationToken);
        using (response)
        {
            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is long length && length > policy.MaxBytes)
            {
                throw new InvalidDataException("The download is larger than expected, so it was refused.");
            }

            if (declaredLength is long expectedLength)
            {
                AppHealth.EnsureFreeSpace(partialPath, expectedLength + 64L * 1024 * 1024);
            }

            using var stallGuard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[256 * 1024];
            long receivedBytes = 0;
            int bytesRead;
            while ((bytesRead = await ReadWithStallGuardAsync(source, buffer, stallGuard, cancellationToken)) > 0)
            {
                receivedBytes += bytesRead;
                if (receivedBytes > policy.MaxBytes)
                {
                    throw new InvalidDataException("The download is larger than expected, so it was stopped.");
                }
                hasher.AppendData(buffer, 0, bytesRead);
                await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                if (declaredLength is long total && total > 0)
                {
                    progress.Report(new ProgressInfo((double)receivedBytes / total,
                        $"Downloading... {receivedBytes / 1e6:0} of {total / 1e6:0} MB"));
                }
                else
                {
                    progress.Report(new ProgressInfo(null, $"Downloading... {receivedBytes / 1e6:0} MB"));
                }
            }

            string actualSha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return new DownloadedFile(actualSha256, serverSha256);
        }
    }

    private static async Task<(HttpResponseMessage Response, string? ServerSha256)> OpenAsync(
        string url, DownloadPolicy policy, CancellationToken cancellationToken)
    {
        string currentUrl = url;
        string? serverSha256 = null;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureAllowed(currentUrl, policy);
            HttpResponseMessage response;
            using (var headerGuard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headerGuard.CancelAfter(StallTimeout);
                try
                {
                    response = await Http.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, headerGuard.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The server took too long to respond.");
                }
            }

            if (serverSha256 == null && response.Headers.TryGetValues("X-Linked-Etag", out var etagValues))
            {
                serverSha256 = NormalizeSha256(etagValues.FirstOrDefault());
            }

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null)
                {
                    throw new InvalidDataException("The server sent a redirect without a destination.");
                }
                currentUrl = new Uri(new Uri(currentUrl), location).ToString();
                continue;
            }

            response.EnsureSuccessStatusCode();
            return (response, serverSha256);
        }
        throw new InvalidDataException("The server redirected too many times.");
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static void EnsureAllowed(string url, DownloadPolicy policy)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Refused a download that is not a plain HTTPS address.");
        }

        string host = uri.Host.ToLowerInvariant();
        bool allowed = policy.AllowedHostSuffixes.Any(suffix => host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal));
        if (!allowed)
        {
            throw new InvalidDataException($"Refused a download from an unexpected server ({host}).");
        }
    }

    private static string? NormalizeSha256(string? headerValue)
    {
        string candidate = (headerValue ?? "").Trim().Trim('"').ToLowerInvariant();
        return candidate.Length == 64 && candidate.All(Uri.IsHexDigit) ? candidate : null;
    }
}
