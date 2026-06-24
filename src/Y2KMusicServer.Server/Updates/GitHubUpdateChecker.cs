using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Y2KMusicServer.Shared;

namespace Y2KMusicServer.Server.Updates;

/// <summary>
/// Polls https://api.github.com/repos/{owner}/{repo}/releases/latest
/// and exposes the cached result as an <see cref="UpdateInfoDto"/>.
///
/// Compares the running assembly's <see cref="Version"/> against
/// the release tag_name (a leading "v" is stripped). The release
/// asset whose name ends with ".zip" is the download URL the
/// installer flow uses.
///
/// The checker never downloads or installs anything — that work
/// lives in the tray, which prompts the user for elevation
/// before running the new installer script.
/// </summary>
public sealed class GitHubUpdateChecker
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GitHubUpdateChecker> _log;
    private readonly string _owner;
    private readonly string _repo;
    private UpdateInfoDto _latest;

    public GitHubUpdateChecker(IHttpClientFactory http,
                               IConfiguration cfg,
                               ILogger<GitHubUpdateChecker> log)
    {
        _http = http;
        _log = log;
        _owner = cfg["Updates:Owner"] ?? "slqdk";
        _repo = cfg["Updates:Repo"] ?? "Y2kMusicServer";
        _latest = new UpdateInfoDto
        {
            Available = false,
            CurrentVersion = CurrentVersion()
        };
    }

    /// <summary>Last cached check result. Never null.</summary>
    public UpdateInfoDto Latest => _latest;

    public async Task<UpdateInfoDto> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion();
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Y2KMusicServer-Updater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(20);

            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            _log.LogDebug("Checking for updates: {Url}", url);

            var release = await client.GetFromJsonAsync<GhRelease>(url, ct);
            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                _log.LogDebug("No release found");
                _latest = new UpdateInfoDto
                {
                    Available = false,
                    CurrentVersion = current,
                    CheckError = null
                };
                return _latest;
            }

            var latestVer = release.TagName.TrimStart('v', 'V');
            var available = CompareSemver(latestVer, current) > 0;

            var asset = release.Assets?
                .FirstOrDefault(a => a.Name?.EndsWith(".zip",
                    StringComparison.OrdinalIgnoreCase) == true);

            _latest = new UpdateInfoDto
            {
                Available = available,
                CurrentVersion = current,
                LatestVersion = latestVer,
                DownloadUrl = asset?.BrowserDownloadUrl,
                ReleaseNotesUrl = release.HtmlUrl,
                PublishedAtUtc = release.PublishedAt
            };

            if (available)
                _log.LogInformation(
                    "Update available: {Latest} (current {Current})",
                    latestVer, current);
            else
                _log.LogDebug(
                    "Up to date: {Current} (latest {Latest})",
                    current, latestVer);

            return _latest;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
            _latest = new UpdateInfoDto
            {
                Available = false,
                CurrentVersion = current,
                CheckError = ex.Message
            };
            return _latest;
        }
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // Returns >0 if a > b, <0 if a < b, 0 if equal.
    // Lenient: accepts "1.2", "1.2.3", "1.2.3.4". Missing components
    // count as zero.
    public static int CompareSemver(string a, string b)
    {
        var pa = ParseSemver(a);
        var pb = ParseSemver(b);
        for (var i = 0; i < 4; i++)
        {
            var cmp = pa[i].CompareTo(pb[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int[] ParseSemver(string s)
    {
        var parts = s.Split('.', '-', '+');
        var v = new int[4];
        for (var i = 0; i < 4 && i < parts.Length; i++)
            int.TryParse(parts[i], out v[i]);
        return v;
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")]   public string? TagName { get; set; }
        [JsonPropertyName("name")]       public string? Name { get; set; }
        [JsonPropertyName("html_url")]   public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")]     public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")]                 public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")]                 public long Size { get; set; }
    }
}
