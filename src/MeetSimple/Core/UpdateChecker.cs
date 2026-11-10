using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeetSimple.Core;

/// <summary>Newest GitHub release that is newer than this build, plus where to download it.</summary>
public sealed record AppUpdateInfo(
    string Tag,
    Version Version,
    string ReleasePageUrl,
    string ZipDownloadUrl,
    string? Sha256DownloadUrl,
    string? Sha256FromBody);

/// <summary>
/// Asks GitHub for the latest Meet Simple release (metadata only). Failures return null —
/// update checks must never block startup with an error dialog.
/// </summary>
public static class UpdateChecker
{
    public const string Owner = "devanshhdalal";
    public const string Repo = "Meet-Simple";
    public const string ZipAssetName = "MeetSimple-win-x64.zip";
    public const string Sha256AssetName = "MeetSimple-win-x64.zip.sha256";

    private const string LatestReleaseUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private const long MaxResponseBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex Sha256Line = new(@"\b([a-fA-F0-9]{64})\b", RegexOptions.CultureInvariant);

    // Must be initialized before Http — CreateClient reads LocalVersionLabel for the User-Agent.
    public static Version LocalVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : new Version(0, 0, 0);

    public static string LocalVersionLabel =>
        $"{LocalVersion.Major}.{LocalVersion.Minor}.{LocalVersion.Build}";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<AppUpdateInfo?> CheckForNewerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            long? length = response.Content.Headers.ContentLength;
            if (length is > MaxResponseBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var limited = new LimitedReadStream(stream, MaxResponseBytes);
            using var document = await JsonDocument.ParseAsync(limited, cancellationToken: timeout.Token);

            var root = document.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;
            string? body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            if (tag == null || htmlUrl == null || !TryParseTagVersion(tag, out var remoteVersion)) return null;
            if (!IsAllowedReleasePage(htmlUrl)) return null;
            if (remoteVersion <= LocalVersion) return null;

            string? zipUrl = null;
            string? shaUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    string? url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.Equals(ZipAssetName, StringComparison.OrdinalIgnoreCase)) zipUrl = url;
                    else if (name.Equals(Sha256AssetName, StringComparison.OrdinalIgnoreCase)) shaUrl = url;
                }
            }

            if (zipUrl == null || !IsAllowedDownloadUrl(zipUrl)) return null;
            if (shaUrl != null && !IsAllowedDownloadUrl(shaUrl)) shaUrl = null;

            string? bodySha = FindSha256InText(body);
            return new AppUpdateInfo(tag.Trim(), remoteVersion, htmlUrl, zipUrl, shaUrl, bodySha);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryParseTagVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        int dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    public static string? FindSha256InText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Sha256Line.Match(text);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static bool IsAllowedReleasePage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && IsGitHubDownloadHost(uri.Host);

    private static bool IsGitHubDownloadHost(string host)
    {
        string lower = host.ToLowerInvariant();
        return lower is "api.github.com" or "github.com"
               || lower.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Public GitHub API — honour the system proxy (unlike localhost Ollama).
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MeetSimple/{LocalVersionLabel}");
        return client;
    }

    /// <summary>Stops a response body from growing past the size we are willing to parse.</summary>
    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int allowed = (int)Math.Min(count, Math.Max(0, maxBytes - read));
            if (allowed == 0) return 0;
            int n = inner.Read(buffer, offset, allowed);
            read += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int allowed = (int)Math.Min(count, Math.Max(0, maxBytes - read));
            if (allowed == 0) return 0;
            int n = await inner.ReadAsync(buffer.AsMemory(offset, allowed), cancellationToken);
            read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int allowed = (int)Math.Min(buffer.Length, Math.Max(0, maxBytes - read));
            if (allowed == 0) return 0;
            int n = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            read += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
