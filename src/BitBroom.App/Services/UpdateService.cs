using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BitBroom.App.Services;

/// <summary>Latest-release metadata relevant to updating.</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleasePageUrl,
    string InstallerName,
    string InstallerUrl,
    string ChecksumsUrl);

/// <summary>
/// GitHub-releases update checker and installer. This is the only network code in
/// BitBroom: a single HTTPS call to the GitHub API (opt-out in Settings), and — only
/// when the user explicitly clicks install — the installer download itself, which is
/// verified against the release's SHA256SUMS.txt before it is executed.
/// </summary>
public static class UpdateService
{
    private const string Owner = "pwnapplehat";
    private const string Repo = "BitBroom";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BitBroom/{CurrentVersion.ToString(3)} (+https://github.com/{Owner}/{Repo})");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Running version. BITBROOM_TEST_CURRENT_VERSION overrides it so the full
    /// update flow can be exercised end-to-end against a real release.
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            string? test = Environment.GetEnvironmentVariable("BITBROOM_TEST_CURRENT_VERSION");
            if (test is not null && Version.TryParse(test, out Version? overridden))
            {
                return Pad(overridden);
            }

            return Pad(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));
        }
    }

    /// <summary>Returns update info when a newer release with installer assets exists, else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream json = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        string? tag = root.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() : null;
        if (tag is null || !TryParseTag(tag, out Version? latest))
        {
            return null;
        }

        if (latest <= CurrentVersion)
        {
            return null;
        }

        string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlEl)
            ? urlEl.GetString() ?? $"https://github.com/{Owner}/{Repo}/releases"
            : $"https://github.com/{Owner}/{Repo}/releases";

        string? installerName = null, installerUrl = null, checksumsUrl = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement dlEl) ? dlEl.GetString() : null;
                if (name is null || url is null)
                {
                    continue;
                }

                if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerName = name;
                    installerUrl = url;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = url;
                }
            }
        }

        // Without a verifiable installer there is nothing to auto-update with;
        // the banner then falls back to the release page.
        return new UpdateInfo(
            latest,
            tag,
            releaseUrl,
            installerName ?? string.Empty,
            installerUrl ?? string.Empty,
            checksumsUrl ?? string.Empty);
    }

    /// <summary>
    /// Downloads the installer, verifies its SHA-256 against the release's
    /// SHA256SUMS.txt and returns the local path. Throws on any mismatch.
    /// </summary>
    public static async Task<string> DownloadVerifiedInstallerAsync(
        UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.InstallerUrl.Length == 0 || update.ChecksumsUrl.Length == 0)
        {
            throw new InvalidOperationException("The release has no verifiable installer asset.");
        }

        // 1. Expected hash from the same release's checksum manifest.
        string manifest = await Http.GetStringAsync(update.ChecksumsUrl, ct).ConfigureAwait(false);
        string? expectedHash = null;
        foreach (string line in manifest.Split('\n'))
        {
            string trimmed = line.Trim();
            int split = trimmed.IndexOf(' ');
            if (split > 0 && trimmed[(split + 1)..].Trim().Equals(update.InstallerName, StringComparison.OrdinalIgnoreCase))
            {
                expectedHash = trimmed[..split].Trim();
                break;
            }
        }

        if (expectedHash is null || expectedHash.Length != 64)
        {
            throw new InvalidOperationException("The release checksum manifest has no entry for the installer.");
        }

        // 2. Streamed download with progress.
        string directory = Path.Combine(Path.GetTempPath(), "BitBroom", "update");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, update.InstallerName);

        using (HttpResponseMessage response = await Http.GetAsync(
            update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;

            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (total is > 0)
                {
                    progress?.Report((double)written / total.Value);
                }
            }
        }

        // 3. Integrity check before anything is executed.
        string actualHash;
        await using (var verify = File.OpenRead(path))
        {
            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct).ConfigureAwait(false));
        }

        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("Installer checksum mismatch — download discarded.");
        }

        return path;
    }

    /// <summary>
    /// Starts the verified installer silently. The caller must shut the app down right
    /// after so the installer (which waits on the app's single-instance mutex) can
    /// replace the binaries; it relaunches BitBroom when done.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            // VERYSILENT + SUPPRESSMSGBOXES: no UI at all. The per-user installer needs no
            // elevation; AppMutex makes it wait for this instance to exit, then it relaunches.
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true,
        });
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        string cleaned = tag.TrimStart('v', 'V').Trim();
        if (Version.TryParse(cleaned, out Version? parsed))
        {
            version = Pad(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Normalizes to three components so 1.1 and 1.1.0 compare as equal.</summary>
    private static Version Pad(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
