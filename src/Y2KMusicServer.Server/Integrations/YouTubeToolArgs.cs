namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// The yt-dlp arguments that have to change as YouTube's blocking changes,
/// gathered in one place so the download queue and the search/fetch path stay in
/// step and neither needs a rebuild to adapt.
///
/// Why this exists: YouTube periodically starts refusing the media URLs that a
/// given player client hands out, while metadata extraction keeps working — the
/// symptom is <c>HTTP Error 403: Forbidden</c> after a clean resolve. The usual
/// escapes are, in order of effort: a newer yt-dlp, a different player client,
/// browser cookies, and a PO-token provider. The first is out of our hands; the
/// rest are settings here, editable from the admin Settings dialog, because the
/// service runs from Program Files where the operator cannot edit
/// appsettings.json.
/// </summary>
public static class YouTubeToolArgs
{
    /// <summary>Player clients tried, in order, after a 403 on the default. The
    /// stored value is a comma-separated list; blank restores this default.</summary>
    public const string DefaultClientFallbacks = "tv,mweb,web_safari";

    /// <summary>
    /// Arguments common to every yt-dlp invocation: the cookies file and any
    /// operator-supplied extra arguments. Applied to searches and downloads alike.
    /// </summary>
    public static List<string> Common(IConfiguration cfg)
    {
        var c = IntegrationsStore.Load(cfg);
        var args = new List<string>();

        var cookies = (c.CookiesFile ?? "").Trim();
        if (cookies.Length > 0 && File.Exists(cookies))
        {
            args.Add("--cookies");
            args.Add(cookies);
        }

        args.AddRange(SplitArgs(c.ExtraYtDlpArgs ?? ""));
        return args;
    }

    /// <summary>
    /// The player clients to try, in order. The first entry is always the empty
    /// string, meaning "yt-dlp's own default" — the fallbacks are only used after
    /// that has been refused.
    /// </summary>
    public static List<string> ClientAttempts(IConfiguration cfg)
    {
        var raw = (IntegrationsStore.Load(cfg).PlayerClientFallbacks ?? "").Trim();
        if (raw.Length == 0) raw = DefaultClientFallbacks;

        var list = new List<string> { "" };
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!list.Contains(part, StringComparer.OrdinalIgnoreCase))
                list.Add(part);
        return list;
    }

    /// <summary>The extractor-args pair that forces one player client, or nothing
    /// for the default attempt.</summary>
    public static IEnumerable<string> ClientArg(string client)
    {
        if (string.IsNullOrWhiteSpace(client)) yield break;
        yield return "--extractor-args";
        yield return $"youtube:player_client={client}";
    }

    /// <summary>
    /// Whether a failure looks like YouTube refusing the media URL rather than a
    /// broken setup — the case another player client might get past. A missing
    /// binary or an unavailable video is NOT retried.
    /// </summary>
    public static bool LooksBlocked(IEnumerable<string> stderrLines)
    {
        foreach (var line in stderrLines)
        {
            if (line.Contains("403", StringComparison.Ordinal)
                || line.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
                || line.Contains("player response", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Splits an operator-typed argument string on spaces, honouring double
    /// quotes so a value containing spaces survives. Arguments go to
    /// ProcessStartInfo.ArgumentList, never through a shell, so nothing here is
    /// re-interpreted after the split.
    /// </summary>
    public static List<string> SplitArgs(string s)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        foreach (var ch in s)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
