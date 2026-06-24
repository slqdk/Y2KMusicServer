using System.Runtime.InteropServices;

namespace Y2KMusicServer.Server.Network;

/// <summary>
/// Authenticates the service's logon session to an SMB server using stored
/// credentials, so the headless LocalSystem service can read music folders on a
/// network share (which it otherwise cannot — LocalSystem has no mapped drives
/// and reaches the network as the machine account).
///
/// Wraps the Win32 <c>WNetAddConnection2</c> API (mpr.dll). The connection is
/// "deviceless" (no drive letter): once established, ordinary UNC file access
/// (<c>Directory.EnumerateDirectories</c>, <c>File.OpenRead</c>) to that server
/// uses the supplied credentials for the lifetime of the process. SMB reuses one
/// authenticated session per server, so connecting to any one share on a server
/// also authorises access to its other shares.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NetworkShareConnector
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<NetworkShareConnector> _log;

    public NetworkShareConnector(IConfiguration cfg, ILogger<NetworkShareConnector> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public sealed record ConnectResult(bool Ok, int Code, string Message);

    /// <summary>
    /// Authenticates the session to the server behind <paramref name="uncPath"/>
    /// with the supplied credentials. <paramref name="uncPath"/> may be a full
    /// path (<c>\\server\share\sub</c>) or a share root; the share root is what
    /// we connect to.
    /// </summary>
    public ConnectResult Connect(string uncPath, string username, string password)
    {
        var shareRoot = ShareRoot(uncPath);
        if (shareRoot is null)
            return new ConnectResult(false, -1, "Not a UNC path (expected \\\\server\\share).");

        var nr = new NETRESOURCE
        {
            dwType = RESOURCETYPE_DISK,
            lpRemoteName = shareRoot
        };

        // CONNECT_TEMPORARY keeps the connection out of any persisted profile
        // (irrelevant for a service, but tidy).
        int code = WNetAddConnection2(ref nr, password, username, CONNECT_TEMPORARY);

        if (code == NO_ERROR || code == ERROR_ALREADY_ASSIGNED || code == ERROR_DEVICE_ALREADY_REMEMBERED)
            return new ConnectResult(true, code, "Connected.");

        // A session to this server with different credentials already exists.
        // Drop it and retry once with the supplied credentials.
        if (code == ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            try { WNetCancelConnection2(shareRoot, 0, true); } catch { /* best-effort */ }
            code = WNetAddConnection2(ref nr, password, username, CONNECT_TEMPORARY);
            if (code == NO_ERROR || code == ERROR_ALREADY_ASSIGNED)
                return new ConnectResult(true, code, "Connected (replaced existing session).");
        }

        var msg = Describe(code);
        _log.LogWarning("WNetAddConnection2 to {Share} failed: {Code} ({Msg})", shareRoot, code, msg);
        return new ConnectResult(false, code, msg);
    }

    /// <summary>
    /// Ensures a connection to the server behind <paramref name="uncPath"/> using
    /// the credential stored for that host, if any. Returns Ok (a no-op) when the
    /// path is local or no credential is stored — callers then just try the path
    /// directly. Used before browsing / scanning network folders.
    /// </summary>
    public ConnectResult EnsureConnected(string uncPath)
    {
        if (ShareRoot(uncPath) is null)
            return new ConnectResult(true, NO_ERROR, "Local path; nothing to connect.");

        var cred = NetworkShareStore.Resolve(_cfg, uncPath);
        if (cred is null)
            return new ConnectResult(true, NO_ERROR, "No stored credential for this host.");

        return Connect(uncPath, cred.Value.Username, cred.Value.Password);
    }

    /// <summary>Drops the authenticated session to the share root behind the path.</summary>
    public void Disconnect(string uncPath)
    {
        var shareRoot = ShareRoot(uncPath);
        if (shareRoot is null) return;
        try { WNetCancelConnection2(shareRoot, 0, true); } catch { /* best-effort */ }
    }

    /// <summary><c>\\server\share\a\b</c> → <c>\\server\share</c>; null if not UNC.
    /// Public so callers (e.g. the reconnect service) can dedupe paths by share root.</summary>
    public static string? ShareRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('/', '\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = p.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;            // need server AND share
        return $@"\\{parts[0]}\{parts[1]}";
    }

    private static string Describe(int code) => code switch
    {
        NO_ERROR => "OK",
        5    => "Access denied — check the username and password.",
        53   => "Network path not found — the server name may be wrong or unreachable.",
        67   => "Network name not found — the share does not exist on that server.",
        86   => "The network password is incorrect.",
        1219 => "Conflicting connection to the server with different credentials.",
        1326 => "Logon failure — bad username or password.",
        _    => $"Windows error {code}."
    };

    // ── Win32 (mpr.dll) ────────────────────────────────────────────
    private const int RESOURCETYPE_DISK = 0x00000001;
    private const int CONNECT_TEMPORARY = 0x00000004;
    private const int NO_ERROR = 0;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_DEVICE_ALREADY_REMEMBERED = 1202;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
