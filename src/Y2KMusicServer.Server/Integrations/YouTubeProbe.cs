using System.Diagnostics;
using System.Text.Json;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// One stage of the YouTube preflight. <see cref="Ok"/> is the stage's own
/// pass/fail; <see cref="Critical"/> marks the stages that must pass for the
/// integration to work at all. A non-critical failure (e.g. a missing JS
/// runtime) is surfaced as a warning and does not by itself fail the overall
/// check — though it will usually make the extract stage fail too.
/// </summary>
public sealed record YouTubeCheckStep(
    string Name,
    bool Ok,
    bool Critical,
    string Detail,
    string? Version = null);

/// <summary>
/// Result of <see cref="YouTubeProbe.CheckAsync"/>. <see cref="Ok"/> is true
/// only when every critical stage passed. Steps are in run order so the admin
/// panel can render them top-to-bottom.
/// </summary>
public sealed class YouTubeCheckResult
{
    public required bool Ok { get; init; }
    public required IReadOnlyList<YouTubeCheckStep> Steps { get; init; }
    public required long ElapsedMs { get; init; }
}

/// <summary>
/// Read-only diagnostic for the (not-yet-enabled) YouTube fetch path. Runs a
/// staged preflight — locate + version yt-dlp / ffmpeg / the JS runtime, ping
/// the PO-token provider, then a dry-run extract that proves the whole chain
/// can still resolve a playable audio URL out of YouTube (the part that SABR /
/// PO-token enforcement breaks). Nothing here downloads media or changes state.
/// It exists so the operator can confirm this moving-target tool stack actually
/// works — in the service's own process context (LocalSystem in production) —
/// before switching the feature on, and to diagnose it when a YouTube-side
/// change breaks it later.
///
/// Every external tool is optional at runtime: if one is missing the matching
/// stage reports it rather than throwing, so a single missing tool never takes
/// the whole check down. Paths come from appsettings.json
/// (Integrations:YouTube:*) and default to bare names resolved via PATH.
/// </summary>
public sealed class YouTubeProbe
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<YouTubeProbe> _log;

    private readonly string _ytDlp;
    private readonly string _ffmpeg;
    private readonly string _deno;
    private readonly string _potUrl;
    private readonly string _testQuery;

    public YouTubeProbe(IHttpClientFactory http, IConfiguration cfg, ILogger<YouTubeProbe> log)
    {
        _http = http;
        _log = log;
        _ytDlp     = cfg["Integrations:YouTube:YtDlpPath"]          ?? "yt-dlp";
        _ffmpeg    = cfg["Integrations:YouTube:FfmpegPath"]         ?? "ffmpeg";
        _deno      = cfg["Integrations:YouTube:DenoPath"]           ?? "deno";
        _potUrl    = cfg["Integrations:YouTube:PoTokenProviderUrl"] ?? "http://127.0.0.1:4416";
        _testQuery = cfg["Integrations:YouTube:TestQuery"]          ?? "ytsearch1:lofi";
    }

    public async Task<YouTubeCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<YouTubeCheckStep>();

        // 1. yt-dlp present + version — the core tool; nothing works without it.
        var yt = await RunAsync(_ytDlp, new[] { "--version" }, TimeSpan.FromSeconds(15), ct);
        bool ytOk = yt.LaunchError is null && yt.ExitCode == 0 && yt.Stdout.Trim().Length > 0;
        steps.Add(new YouTubeCheckStep(
            "yt-dlp", ytOk, Critical: true,
            ytOk ? "Found and runnable." : LaunchDetail(_ytDlp, yt),
            ytOk ? yt.Stdout.Trim() : null));

        // 2. ffmpeg present + version — needed to extract/transcode the download
        //    into a file the audio engine can decode. Not required to *resolve* a
        //    track, so its failure is reported on its own line but still critical
        //    (a resolved track that can't be turned into a playable file is no
        //    use).
        var ff = await RunAsync(_ffmpeg, new[] { "-version" }, TimeSpan.FromSeconds(15), ct);
        bool ffOk = ff.LaunchError is null && ff.ExitCode == 0 && ff.Stdout.Length > 0;
        steps.Add(new YouTubeCheckStep(
            "ffmpeg", ffOk, Critical: true,
            ffOk ? "Found and runnable." : LaunchDetail(_ffmpeg, ff),
            ffOk ? FirstLine(ff.Stdout) : null));

        // 3. JS runtime (Deno) — yt-dlp needs an external JS engine to solve
        //    YouTube's nsig challenge; without it extraction usually 403s.
        //    Non-critical on its own (a given build may ship/find another solver),
        //    but a missing runtime is a common cause of a failed extract below.
        var dn = await RunAsync(_deno, new[] { "--version" }, TimeSpan.FromSeconds(15), ct);
        bool dnOk = dn.LaunchError is null && dn.ExitCode == 0 && dn.Stdout.Length > 0;
        steps.Add(new YouTubeCheckStep(
            "JS runtime (Deno)", dnOk, Critical: false,
            dnOk ? "Found and runnable."
                 : $"Not found ({_deno}). yt-dlp needs a JS runtime to solve signatures; extraction will usually fail without one.",
            dnOk ? FirstLine(dn.Stdout) : null));

        // 4. PO-token provider — the local helper that mints the Proof-of-Origin
        //    tokens YouTube now demands. Non-critical (some clients/videos still
        //    resolve without), but its absence is the other usual failure cause.
        bool potOk = await PingAsync(_potUrl, ct);
        steps.Add(new YouTubeCheckStep(
            "PO-token provider", potOk, Critical: false,
            potOk ? $"Reachable at {_potUrl}."
                  : $"Not reachable at {_potUrl}. Start bgutil-ytdlp-pot-provider, or extraction may be blocked/downgraded.",
            null));

        // 5. Dry-run extract — the real test. Ask yt-dlp to resolve one search
        //    result to JSON WITHOUT downloading, then confirm it handed back an
        //    audio format that still carries a media URL. SABR / missing-PO-token
        //    failures show up precisely as audio formats with their URL stripped,
        //    so "an audio format with a url" is the signal the chain still works.
        if (ytOk)
        {
            var ex = await RunAsync(_ytDlp,
                new[] { "-J", "--no-warnings", _testQuery },
                TimeSpan.FromSeconds(60), ct);
            var (exOk, detail) = InterpretExtract(ex);
            steps.Add(new YouTubeCheckStep(
                "Resolve a track (dry run)", exOk, Critical: true, detail, null));
        }
        else
        {
            steps.Add(new YouTubeCheckStep(
                "Resolve a track (dry run)", false, Critical: true,
                "Skipped — yt-dlp not runnable.", null));
        }

        sw.Stop();
        bool overall = steps.Where(s => s.Critical).All(s => s.Ok);
        _log.LogInformation(
            "YouTube preflight: {Result} in {Ms}ms ({Pass}/{Total} steps passed)",
            overall ? "OK" : "FAILED", sw.ElapsedMilliseconds,
            steps.Count(s => s.Ok), steps.Count);

        return new YouTubeCheckResult
        {
            Ok = overall,
            Steps = steps,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    // ── interpretation helpers ────────────────────────────────────────────

    private static (bool ok, string detail) InterpretExtract(ProcResult ex)
    {
        if (ex.LaunchError != null) return (false, ex.LaunchError);
        if (ex.TimedOut) return (false, "Timed out resolving the test query (60s).");
        if (ex.Stdout.Trim().Length == 0) return (false, ExtractErrorSummary(ex));

        try
        {
            using var doc = JsonDocument.Parse(ex.Stdout);
            var root = doc.RootElement;

            // A search returns a playlist object with an entries array; a direct
            // URL returns the entry itself. Normalise to the first entry.
            JsonElement entry = root;
            if (root.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array
                && entries.GetArrayLength() > 0)
            {
                entry = entries[0];
            }

            string? title = entry.TryGetProperty("title", out var ti) ? ti.GetString() : null;

            if (!entry.TryGetProperty("formats", out var formats)
                || formats.ValueKind != JsonValueKind.Array
                || formats.GetArrayLength() == 0)
            {
                return (false, title is null
                    ? "Resolved JSON but found no formats (likely SABR / missing PO token)."
                    : $"Resolved \"{title}\" but it exposed no formats (likely SABR / missing PO token).");
            }

            bool audioWithUrl = false;
            foreach (var f in formats.EnumerateArray())
            {
                string? acodec = f.TryGetProperty("acodec", out var a) ? a.GetString() : null;
                string? url = f.TryGetProperty("url", out var u) ? u.GetString() : null;
                bool hasAudio = acodec is not null && acodec != "none";
                if (hasAudio && !string.IsNullOrEmpty(url)) { audioWithUrl = true; break; }
            }

            if (audioWithUrl)
                return (true, title is null
                    ? "Resolved a playable audio stream."
                    : $"Resolved \"{title}\" with a playable audio stream.");

            return (false, title is null
                ? "Resolved a result but no audio format had a media URL (SABR / PO-token block)."
                : $"Resolved \"{title}\" but no audio format had a media URL (SABR / PO-token block).");
        }
        catch (JsonException)
        {
            return (false, ExtractErrorSummary(ex));
        }
    }

    private static string ExtractErrorSummary(ProcResult ex)
    {
        var err = FirstMeaningfulLine(ex.Stderr);
        return err.Length > 0
            ? $"yt-dlp failed: {err}"
            : $"yt-dlp exited {ex.ExitCode} with no parseable output.";
    }

    private async Task<bool> PingAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            // Any HTTP response (even 404) proves something is listening there.
            using var resp = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string LaunchDetail(string exe, ProcResult r)
        => r.LaunchError
           ?? (r.TimedOut ? "Timed out." : $"Ran but exited {r.ExitCode} ({exe}).");

    private static string FirstLine(string s)
    {
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        return (i < 0 ? s : s[..i]).Trim();
    }

    private static string FirstMeaningfulLine(string s)
    {
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return string.Empty;
    }

    // ── process runner ────────────────────────────────────────────────────

    private sealed record ProcResult(
        int ExitCode, string Stdout, string Stderr, bool TimedOut, string? LaunchError);

    /// <summary>
    /// Runs an external tool, capturing stdout/stderr, with a hard timeout. A
    /// missing binary (or any launch failure) returns a result carrying a
    /// human-readable <see cref="ProcResult.LaunchError"/> instead of throwing,
    /// so a single missing tool never fails the whole check. stdout/stderr are
    /// read asynchronously before waiting for exit to avoid the classic
    /// full-pipe deadlock.
    /// </summary>
    private static async Task<ProcResult> RunAsync(
        string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            // e.g. Win32Exception "The system cannot find the file specified".
            return new ProcResult(-1, "", "", false,
                $"Not found or not runnable ({exe}): {ex.Message}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            string partial = await SafeRead(stdoutTask);
            return new ProcResult(-1, partial, "", TimedOut: true, LaunchError: null);
        }

        string outStr = await stdoutTask;
        string errStr = await stderrTask;
        return new ProcResult(proc.ExitCode, outStr, errStr, false, null);
    }

    private static async Task<string> SafeRead(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }
}
