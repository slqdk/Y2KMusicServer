using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// The search rule shared by the admin library grid and the listener page:
/// every word the person typed must appear SOMEWHERE on the row — title,
/// artist or album — but the words need not be adjacent or in order, and need
/// not all land in the same field.
///
/// That's what makes "metallica puppets" find <i>Metallica — Master of
/// Puppets</i>: "metallica" matches the artist, "puppets" the title. Matching
/// the whole phrase literally (the old rule) found nothing, because no single
/// field contains "metallica puppets".
/// </summary>
public static class TrackSearch
{
    /// <summary>More words than this is noise; the rest are ignored.</summary>
    private const int MaxTokens = 8;

    /// <summary>
    /// Splits the query into distinct search words. LIKE wildcards are stripped
    /// rather than escaped: a stray <c>%</c> would otherwise match everything
    /// and make the search look broken.
    /// </summary>
    public static List<string> Tokens(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return new List<string>();
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("%", "").Replace("_", "").Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTokens)
            .ToList();
    }

    /// <summary>
    /// Narrows the query so every token hits title, artist or album. One AND-ed
    /// Where per token, so SQLite still does the work — no client-side scan.
    /// A blank query is returned unchanged.
    /// </summary>
    public static IQueryable<Track> WhereAllTokens(this IQueryable<Track> query, string? q)
    {
        foreach (var token in Tokens(q))
        {
            var tok = token;   // capture per iteration, not the loop variable
            query = query.Where(t =>
                (t.Title != null && EF.Functions.Like(t.Title, $"%{tok}%")) ||
                (t.Artist != null && EF.Functions.Like(t.Artist, $"%{tok}%")) ||
                (t.Album != null && EF.Functions.Like(t.Album, $"%{tok}%")));
        }
        return query;
    }

    /// <summary>In-memory twin of the rule, for lists already loaded.</summary>
    public static bool MatchesAllTokens(Track t, IReadOnlyList<string> tokens)
    {
        foreach (var tok in tokens)
        {
            bool hit =
                (t.Title ?? "").Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                (t.Artist ?? "").Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                (t.Album ?? "").Contains(tok, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }
}
