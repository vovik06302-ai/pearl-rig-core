using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PearlRigCore.Services;

public sealed record ReleaseUpdate(string Version, Uri InstallerUri, long SizeBytes, string? Sha256);
public sealed record DownloadProgress(long BytesReceived, long TotalBytes)
{
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100) : 0;
}

public sealed class UpdateService(HttpClient httpClient)
{
    private const string LatestReleaseEndpoint = "https://api.github.com/repos/vovik06302-ai/pearl-rig-core/releases/latest";

    public async Task<ReleaseUpdate?> CheckAsync(Version installedVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd($"PearlRigControl/{installedVersion.ToString(3)}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("В GitHub ещё нет опубликованного стабильного релиза.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = ReadString(root, "tag_name");
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var latestVersion))
            throw new InvalidOperationException("GitHub вернул релиз с некорректным номером версии.");
        if (latestVersion <= installedVersion) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Для версии {latestVersion} в GitHub ещё не загружен установщик.");
        var installer = assets.EnumerateArray().FirstOrDefault(asset =>
            string.Equals(ReadString(asset, "name"), "PearlRigControl-Setup.exe", StringComparison.OrdinalIgnoreCase));
        if (installer.ValueKind != JsonValueKind.Object ||
            !Uri.TryCreate(ReadString(installer, "browser_download_url"), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"У релиза {latestVersion} нет корректного файла PearlRigControl-Setup.exe.");

        var size = installer.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
        var digest = ReadString(installer, "digest");
        var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        return new ReleaseUpdate(latestVersion.ToString(3), uri, size, sha256);
    }

    public async Task<string> DownloadInstallerAsync(ReleaseUpdate release, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"PearlRigControl-Setup-v{release.Version}.exe");
        using var response = await httpClient.GetAsync(release.InstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? release.SizeBytes;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress.Report(new DownloadProgress(received, totalBytes));
            }
            await output.FlushAsync(cancellationToken);
            if (totalBytes > 0 && received != totalBytes)
                throw new InvalidDataException($"Загрузка неполная: получено {received} из {totalBytes} байт.");
        }

        if (!string.IsNullOrWhiteSpace(release.Sha256))
        {
            await using var file = File.OpenRead(filePath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
                throw new InvalidDataException("SHA-256 установщика не совпал с контрольной суммой GitHub Release.");
            }
        }
        return filePath;
    }

    private static string? ReadString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
