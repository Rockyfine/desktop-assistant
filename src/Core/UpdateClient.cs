using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopAssistant.Core;

public sealed class UpdateClient
{
    public sealed record Release(Version Version, long Size, byte[] Sha256);

    private sealed record Manifest(string Version, long Size, string Sha256);

    public const string ManifestName = "update-windows-x64.json";
    private const int ManifestLimit = 16 * 1024;
    private const long InstallerLimit = 512L * 1024 * 1024;
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;
    private readonly string _installerName;
    public Uri ReleasesPage { get; }

    public UpdateClient(string repository, string installerName) : this(SharedHttp, repository, installerName) { }

    internal UpdateClient(HttpClient http, string repository, string installerName)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z") ||
            repository.EndsWith("/.", StringComparison.Ordinal) || repository.EndsWith("/..", StringComparison.Ordinal) ||
            !Regex.IsMatch(installerName, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,49}\z"))
            throw new ArgumentException("更新发布源的配置无效。");

        _http = http;
        _installerName = installerName;
        ReleasesPage = new Uri($"https://github.com/{repository}/releases/");
    }

    public async Task<Release?> CheckAsync(string installedVersion, CancellationToken token)
    {
        Version current = ParseVersion(installedVersion);
        using HttpResponseMessage response = await GetAsync(new Uri(ReleasesPage, $"latest/download/{ManifestName}"), token);
        using var buffer = new MemoryStream();
        await CopyAsync(response.Content, buffer, ManifestLimit, null, null, token);
        buffer.Position = 0;
        Manifest? manifest = await JsonSerializer.DeserializeAsync<Manifest>(buffer, JsonDefaults.Options, token);
        if (manifest?.Sha256 is not { Length: 64 } hash || manifest.Size is <= 0 or > InstallerLimit || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新清单缺少有效的大小或 SHA-256 校验值。");

        Version published = ParseVersion(manifest.Version);
        return published > current ? new Release(published, manifest.Size, Convert.FromHexString(hash)) : null;
    }

    public async Task<string> DownloadAsync(Release release, string directory, IProgress<long>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, $"{_installerName}-Update.exe");
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
        var url = new Uri(ReleasesPage, $"download/v{release.Version}/{_installerName}-Setup-{release.Version}.exe");
        try
        {
            using HttpResponseMessage response = await GetAsync(url, token);
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("更新包大小与发布清单不符。");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                written = await CopyAsync(response.Content, output, release.Size, hash, progress, token);

            if (written != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), release.Sha256))
                throw new InvalidDataException("更新包校验失败，请重新检查更新。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Version ParseVersion(string version) =>
        Version.TryParse(version, out Version? result) && result.Build >= 0 && result.Revision == -1 && result.ToString() == version
            ? result : throw new InvalidDataException("更新版本号无效。");

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (int i = 0; i < 6; ++i)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.IdnHost is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("更新地址未指向 GitHub 发布服务。");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DesktopAssistant-Updater/1.0");
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                using (response)
                {
                    Uri location = response.Headers.Location ?? throw new InvalidDataException("GitHub 更新地址无效。");
                    uri = new Uri(uri, location);
                }
                continue;
            }

            if (response.IsSuccessStatusCode)
                return response;

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("发布页尚未提供此更新文件。");
                throw new HttpRequestException($"GitHub 更新服务返回 HTTP {(int)response.StatusCode}。");
            }
        }
        throw new InvalidDataException("GitHub 更新地址重定向次数过多。");
    }

    private static async Task<long> CopyAsync(HttpContent content, Stream output, long limit,
        IncrementalHash? hash, IProgress<long>? progress, CancellationToken token)
    {
        await using Stream input = await content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[65536];
        long total = 0;
        long lastReport = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidDataException("更新文件超出了预期大小。");

            hash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (total - lastReport >= 1024 * 1024 || total == limit)
            {
                progress?.Report(total);
                lastReport = total;
            }
        }
        return total;
    }
}
