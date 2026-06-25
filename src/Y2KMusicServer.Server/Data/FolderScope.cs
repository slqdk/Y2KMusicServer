using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Maps tracks to the assigned folder that owns them, purely by file location,
/// with "innermost folder wins": when one assigned folder is nested inside
/// another, a track that lives under the inner folder belongs to the inner one.
/// A <see cref="Track"/> has no FolderId (the no-migrations rule rules out the
/// column), so ownership is derived from <see cref="Track.FilePath"/> against the
/// set of assigned folder paths — which is reliable because the scanner builds
/// each FilePath by walking its folder.
/// </summary>
public static class FolderScope
{
    /// <summary>
    /// A folder path normalised to a prefix with a trailing separator, so it
    /// matches files inside the folder but not a sibling that merely shares a name
    /// stem (<c>…\Pop\</c> won't match <c>…\Pop2\</c>).
    /// </summary>
    public static string Prefix(string folderPath)
        => folderPath.TrimEnd('\\', '/') + "\\";

    /// <summary>
    /// The prefixes of assigned folders nested strictly inside
    /// <paramref name="folderPath"/>. Files those cover belong to the deeper
    /// folder, so they are excluded from this one ("innermost wins").
    /// </summary>
    public static List<string> NestedPrefixes(string folderPath, IEnumerable<string> allFolderPaths)
    {
        var self = Prefix(folderPath);
        return allFolderPaths
            .Select(Prefix)
            .Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase)
                        && p.StartsWith(self, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Narrows a track query to those a folder owns: under the folder, minus
    /// anything a deeper assigned folder owns. EF translates each
    /// <see cref="string.StartsWith(string)"/> to an escaped <c>LIKE</c>, so this
    /// composes into SQL (and into a bulk <c>ExecuteDelete</c>).
    /// </summary>
    public static IQueryable<Track> OwnedBy(this IQueryable<Track> tracks, string folderPath, IReadOnlyList<string> nestedPrefixes)
    {
        var self = Prefix(folderPath);
        var q = tracks.Where(t => t.FilePath.StartsWith(self));
        foreach (var p in nestedPrefixes)
        {
            var pre = p; // capture per-iteration for the closure
            q = q.Where(t => !t.FilePath.StartsWith(pre));
        }
        return q;
    }
}
