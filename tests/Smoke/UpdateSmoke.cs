using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopAssistant.Core;

internal static class UpdateSmoke
{
    private const string Repository = "example/desktop-assistant";
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("verified installer fixture");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Installer));

    public static async Task CheckAsync(string scratch)
    {
        CancellationToken token = CancellationToken.None;
        var handler = new ReleaseHandler();
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, Repository, "DesktopAssistant");
        string target = Path.Combine(scratch, "updates");

        UpdateClient.Release release = await client.CheckAsync("1.3.0", token)
            ?? throw new InvalidOperationException("The release should be newer.");
        Check(release.Version.ToString() == "1.10.0", "numeric version comparison");
        Check(await client.CheckAsync("1.10.0", token) is null, "no reinstall for current release");
        Check(await client.CheckAsync("1.11.0", token) is null, "no downgrade");
        string path = await client.DownloadAsync(release, target, null, token);
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(Installer), "verified installer persisted");
        Check(Directory.EnumerateFiles(target).Count() == 1, "no partial download on success");

        handler.Asset = Installer.Select(item => (byte)(item ^ 1)).ToArray();
        await ExpectAsync<InvalidDataException>(() => client.DownloadAsync(release, target, null, token));
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(Installer), "bad hash leaves verified file intact");
        Check(Directory.EnumerateFiles(target).Count() == 1, "partial file cleared on failure");

        handler.Manifest = JsonSerializer.Serialize(new { version = "1.10.0", size = Installer.Length, sha256 = "bad" });
        await ExpectAsync<InvalidDataException>(() => client.CheckAsync("1.3.0", token));
        handler.Manifest = new string('a', 17000);
        await ExpectAsync<InvalidDataException>(() => client.CheckAsync("1.3.0", token));

        handler.ExternalRedirect = true;
        await ExpectAsync<InvalidDataException>(() => client.CheckAsync("1.3.0", token));
        Console.WriteLine("Update feed, version comparison, HTTPS redirect isolation and download verification OK.");
    }

    private static void Check(bool valid, string feature)
    {
        if (!valid)
            throw new InvalidOperationException("Update check failed: " + feature);
    }

    private static async Task ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try { await operation(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        public string Manifest { get; set; } =
            JsonSerializer.Serialize(new { version = "1.10.0", size = Installer.Length, sha256 = Digest });
        public byte[] Asset { get; set; } = Installer;
        public bool ExternalRedirect { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = request.RequestUri?.GetLeftPart(UriPartial.Path) ?? "";
            HttpResponseMessage response = url switch
            {
                $"https://github.com/{Repository}/releases/latest/download/update-windows-x64.json"
                    => Redirect(ExternalRedirect ? "https://example.net/fake.json" : "https://release-assets.githubusercontent.com/manifest"),
                $"https://github.com/{Repository}/releases/download/v1.10.0/DesktopAssistant-Setup-1.10.0.exe"
                    => Redirect("https://objects.githubusercontent.com/installer"),
                "https://release-assets.githubusercontent.com/manifest"
                    => new(HttpStatusCode.OK) { Content = new StringContent(Manifest) },
                "https://objects.githubusercontent.com/installer"
                    => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Asset) },
                _ => throw new InvalidOperationException("Unexpected update request: " + url)
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Redirect(string uri) =>
            new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(uri) } };
    }
}
