using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Network;

/// <summary>
/// Stored SMB credentials for network music folders, persisted as JSON on disk
/// (<c>&lt;DataPath&gt;\network-shares.json</c>) — not in the database, per the
/// no-migrations rule (persistence.md). One credential per server host
/// (<c>\\server</c>), reused across every share / folder on that host (SMB
/// authenticates a session per server).
///
/// The password is encrypted with Windows DPAPI at <b>LocalMachine</b> scope so
/// the LocalSystem service can decrypt it on any later run. LocalMachine (not
/// CurrentUser) is required: the encrypting and decrypting identity is the
/// machine's SYSTEM account and the value must survive service restarts.
///
/// SECURITY: LocalMachine DPAPI means anything running as SYSTEM / Administrator
/// on THIS machine can decrypt these passwords — the inherent cost of a headless
/// service that re-authenticates to shares unattended. The password is never
/// returned through any API; only the host + username are ever surfaced.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class NetworkShareStore
{
    public sealed class Entry
    {
        public string Host { get; set; } = "";
        public string Username { get; set; } = "";
        /// <summary>Base64 of the DPAPI-protected (LocalMachine) UTF-8 password.</summary>
        public string ProtectedPassword { get; set; } = "";
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class StoreFile
    {
        public List<Entry> Shares { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>
    /// Normalises a host or UNC path to the bare server name, lower-cased:
    /// <c>\\NAS\Music\x</c> → <c>nas</c>, <c>NAS</c> → <c>nas</c>.
    /// </summary>
    public static string NormaliseHost(string hostOrPath)
    {
        if (string.IsNullOrWhiteSpace(hostOrPath)) return "";
        var s = hostOrPath.Trim().Replace('/', '\\').TrimStart('\\');
        var slash = s.IndexOf('\\');
        if (slash >= 0) s = s.Substring(0, slash);
        return s.ToLowerInvariant();
    }

    private static StoreFile LoadFile(IConfiguration cfg)
    {
        var path = DataPaths.NetworkSharesPath(cfg);
        try
        {
            if (File.Exists(path))
            {
                var f = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path));
                if (f != null) return f;
            }
        }
        catch { /* missing / corrupt → empty */ }
        return new StoreFile();
    }

    private static void SaveFile(IConfiguration cfg, StoreFile f)
    {
        var path = DataPaths.NetworkSharesPath(cfg);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(f, Indented));
    }

    /// <summary>Hosts + usernames currently stored (NEVER the password).</summary>
    public static IReadOnlyList<(string Host, string Username)> List(IConfiguration cfg)
    {
        lock (Gate)
            return LoadFile(cfg).Shares
                .Select(e => (e.Host, e.Username))
                .ToList();
    }

    /// <summary>
    /// Stores (or replaces) the credential for a host. The password is
    /// DPAPI-encrypted before it touches disk.
    /// </summary>
    public static void Upsert(IConfiguration cfg, string hostOrPath, string username, string password)
    {
        var host = NormaliseHost(hostOrPath);
        if (host.Length == 0) throw new ArgumentException("host is required", nameof(hostOrPath));

        var protectedPw = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(password ?? ""), null,
                DataProtectionScope.LocalMachine));

        lock (Gate)
        {
            var f = LoadFile(cfg);
            f.Shares.RemoveAll(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
            f.Shares.Add(new Entry
            {
                Host = host,
                Username = username ?? "",
                ProtectedPassword = protectedPw,
                AddedUtc = DateTime.UtcNow
            });
            SaveFile(cfg, f);
        }
    }

    /// <summary>Removes the credential for a host. Returns true if one was present.</summary>
    public static bool Remove(IConfiguration cfg, string hostOrPath)
    {
        var host = NormaliseHost(hostOrPath);
        lock (Gate)
        {
            var f = LoadFile(cfg);
            var removed = f.Shares.RemoveAll(
                e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveFile(cfg, f);
            return removed;
        }
    }

    /// <summary>
    /// Resolves the stored username + decrypted password for a host, or null if
    /// none is stored / decryption fails.
    /// </summary>
    public static (string Username, string Password)? Resolve(IConfiguration cfg, string hostOrPath)
    {
        var host = NormaliseHost(hostOrPath);
        lock (Gate)
        {
            var e = LoadFile(cfg).Shares
                .FirstOrDefault(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            try
            {
                var pw = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(Convert.FromBase64String(e.ProtectedPassword), null,
                        DataProtectionScope.LocalMachine));
                return (e.Username, pw);
            }
            catch { return null; }
        }
    }
}
