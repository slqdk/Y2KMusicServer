using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Cast;

/// <summary>
/// Plays the live <c>/stream</c> broadcast on Google Cast devices (Google Home
/// / Nest speakers, Chromecast Audio, Cast-enabled TVs).
///
/// The audio never passes through this process twice: the speaker is told a URL
/// and fetches the MP3 stream itself, exactly like a browser tab pointed at
/// <c>/stream</c>. That means the URL must resolve from the SPEAKER's network —
/// the server's LAN address, not a reverse-proxy hostname unless that resolves
/// internally (see <see cref="StreamUrl"/> and the config override).
///
/// Discovery is mDNS (UDP 5353), which the Windows firewall must allow for the
/// service process, and which does not cross subnets/VLANs. Speakers that were
/// seen once are remembered with their address, so casting still works when a
/// later discovery pass misses them.
///
/// Everything here is opt-in: casting is refused unless the feature is enabled
/// AND the target speaker is on the operator's allow-list.
/// </summary>
public sealed class CastService : IDisposable
{
    /// <summary>Google's Default Media Receiver — plays plain media URLs.</summary>
    private const string DefaultMediaReceiverAppId = "CC1AD845";

    private readonly IConfiguration _cfg;
    private readonly AudioEngine _engine;
    private readonly ILogger<CastService> _log;

    // One gate for connect/cast/stop: the Cast protocol is a stateful socket
    // per device and the admin UI can fire several buttons at once.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChromecastReceiver> _found = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastScanUtc = DateTime.MinValue;
    private bool _disposed;

    private sealed class Session
    {
        public required ChromecastClient Client { get; init; }
        public required string Name { get; init; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    }

    public CastService(IConfiguration cfg, AudioEngine engine, ILogger<CastService> log)
    {
        _cfg = cfg;
        _engine = engine;
        _log = log;
    }

    /// <summary>A speaker as reported to the UI.</summary>
    public sealed record CastDeviceDto(
        string Id, string Name, string Model, string Host, int Port,
        bool Allowed, bool Online, bool Casting);

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds speakers via mDNS and merges what it sees into the remembered list
    /// (names and addresses refresh; the Allowed flag is never touched). A
    /// cached result is reused for 60 s unless <paramref name="force"/> is set,
    /// since each scan costs a few seconds of multicast chatter.
    /// </summary>
    public async Task<IReadOnlyList<CastDeviceDto>> DiscoverAsync(bool force = false)
    {
        var cfg = CastConfigStore.Load(_cfg);
        if (!cfg.Enabled) return Snapshot(cfg);

        if (force || DateTime.UtcNow - _lastScanUtc > TimeSpan.FromSeconds(60))
        {
            try
            {
                using var locator = new ChromecastLocator();
                var receivers = await locator.FindReceiversAsync(
                    fullTimeout: TimeSpan.FromSeconds(4)).ConfigureAwait(false);

                lock (_found)
                {
                    _found.Clear();
                    foreach (var r in receivers)
                    {
                        var key = KeyOf(r);
                        if (key.Length > 0) _found[key] = r;
                    }
                }
                _lastScanUtc = DateTime.UtcNow;

                // Remember what we saw (address + friendly name), preserving Allowed.
                cfg = CastConfigStore.Update(_cfg, c =>
                {
                    lock (_found)
                    {
                        foreach (var (key, r) in _found)
                        {
                            var known = c.Speakers.FirstOrDefault(s =>
                                string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase));
                            if (known == null)
                            {
                                known = new CastConfigStore.CastSpeaker { Id = key, Allowed = false };
                                c.Speakers.Add(known);
                            }
                            known.Name = string.IsNullOrWhiteSpace(r.Name) ? known.Name : r.Name;
                            known.Model = r.Model ?? known.Model;
                            known.Host = r.DeviceUri?.Host ?? known.Host;
                            known.Port = r.Port > 0 ? r.Port : known.Port;
                        }
                    }
                });

                _log.LogInformation("Cast discovery found {Count} device(s).", _found.Count);
            }
            catch (Exception ex)
            {
                // A failed scan must not wipe the remembered list — the operator
                // can still cast to a known address.
                _log.LogWarning(ex, "Cast discovery failed (mDNS blocked, or no devices on this subnet).");
            }
        }

        return Snapshot(cfg);
    }

    private IReadOnlyList<CastDeviceDto> Snapshot(CastConfigStore.CastConfig cfg)
    {
        var list = new List<CastDeviceDto>();
        foreach (var s in cfg.Speakers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool online;
            lock (_found) online = _found.ContainsKey(s.Id);
            bool casting;
            lock (_sessions) casting = _sessions.ContainsKey(s.Id);
            list.Add(new CastDeviceDto(s.Id, s.Name, s.Model, s.Host, s.Port, s.Allowed, online, casting));
        }
        return list;
    }

    /// <summary>The remembered speakers without touching the network.</summary>
    public IReadOnlyList<CastDeviceDto> Known() => Snapshot(CastConfigStore.Load(_cfg));

    // ── Casting ──────────────────────────────────────────────────────────────

    public sealed record CastResult(bool Ok, string Message, string? StreamUrl = null);

    /// <summary>
    /// Starts (or restarts) the live stream on one speaker. Refuses unless
    /// casting is enabled and the speaker is allow-listed.
    /// </summary>
    public async Task<CastResult> PlayAsync(string deviceId, CancellationToken ct = default)
    {
        var cfg = CastConfigStore.Load(_cfg);
        if (!cfg.Enabled) return new CastResult(false, "Casting is switched off.");

        var speaker = cfg.Speakers.FirstOrDefault(s =>
            string.Equals(s.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (speaker == null) return new CastResult(false, "Unknown speaker — run a discovery first.");
        if (!speaker.Allowed) return new CastResult(false, $"{speaker.Name} is not allowed to be used.");

        var receiver = await ResolveAsync(speaker).ConfigureAwait(false);
        if (receiver == null)
            return new CastResult(false, $"{speaker.Name} was not reachable (not on this network right now).");

        var url = StreamUrl(cfg);
        var status = _engine.GetStatus();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(speaker.Id).ConfigureAwait(false);

            var client = new ChromecastClient();
            await client.ConnectChromecast(receiver).ConfigureAwait(false);
            await client.LaunchApplicationAsync(DefaultMediaReceiverAppId).ConfigureAwait(false);

            if (cfg.Volume > 0)
            {
                try { await client.ReceiverChannel.SetVolume(cfg.Volume).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug(ex, "Cast volume set failed on {Name}", speaker.Name); }
            }

            var media = new Media
            {
                ContentUrl = url,
                ContentType = "audio/mpeg",
                StreamType = StreamType.Live,
                Metadata = new MusicTrackMetadata
                {
                    Title = string.IsNullOrWhiteSpace(status.Title) ? "Y2K Music Server" : status.Title,
                    Artist = status.Artist ?? ""
                }
            };
            await client.MediaChannel.LoadAsync(media).ConfigureAwait(false);

            client.Disconnected += (_, _) => Forget(speaker.Id);
            lock (_sessions) _sessions[speaker.Id] = new Session { Client = client, Name = speaker.Name };

            _log.LogInformation("Casting {Url} to {Name}.", url, speaker.Name);
            return new CastResult(true, $"Playing on {speaker.Name}.", url);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cast to {Name} failed.", speaker.Name);
            return new CastResult(false, $"Could not start {speaker.Name}: {ex.Message}", url);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the cast on one speaker (best effort; always succeeds).</summary>
    public async Task<CastResult> StopAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var name = await StopUnlockedAsync(deviceId).ConfigureAwait(false);
            return new CastResult(true, name == null ? "Nothing was playing there." : $"Stopped {name}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops every cast this server started (the panic button).</summary>
    public async Task<int> StopAllAsync(CancellationToken ct = default)
    {
        string[] ids;
        lock (_sessions) ids = _sessions.Keys.ToArray();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int n = 0;
            foreach (var id in ids)
                if (await StopUnlockedAsync(id).ConfigureAwait(false) != null) n++;
            return n;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>. Returns the name it stopped, or null.</summary>
    private async Task<string?> StopUnlockedAsync(string deviceId)
    {
        Session? s;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(deviceId, out s)) return null;
            _sessions.Remove(deviceId);
        }

        try { await s.Client.MediaChannel.StopAsync().ConfigureAwait(false); } catch { }
        try { await s.Client.ReceiverChannel.StopApplication().ConfigureAwait(false); } catch { }
        try { await s.Client.DisconnectAsync().ConfigureAwait(false); } catch { }
        _log.LogInformation("Stopped casting to {Name}.", s.Name);
        return s.Name;
    }

    private void Forget(string deviceId)
    {
        lock (_sessions) _sessions.Remove(deviceId);
    }

    /// <summary>True when this server currently has a cast running on that speaker.</summary>
    public bool IsCasting(string deviceId)
    {
        lock (_sessions) return _sessions.ContainsKey(deviceId);
    }

    // ── Addresses ────────────────────────────────────────────────────────────

    /// <summary>
    /// The URL handed to the speakers: the configured override, else
    /// <c>http://&lt;LAN ip&gt;:&lt;Kestrel port&gt;/stream</c>.
    /// </summary>
    public string StreamUrl(CastConfigStore.CastConfig? cfg = null)
    {
        cfg ??= CastConfigStore.Load(_cfg);
        if (!string.IsNullOrWhiteSpace(cfg.StreamUrl)) return cfg.StreamUrl.Trim();
        return $"http://{LocalIPv4()}:{KestrelPort()}/stream";
    }

    private int KestrelPort()
    {
        var url = _cfg["Kestrel:Endpoints:Http:Url"];
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port > 0)
            return u.Port;
        return 8765;
    }

    /// <summary>
    /// The server's LAN IPv4. Uses a connectionless UDP socket to learn which
    /// interface the OS would route from (no packet is sent), falling back to
    /// the first up, non-loopback IPv4 address.
    /// </summary>
    public static string LocalIPv4()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
            if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                return ep.Address.ToString();
        }
        catch { /* no default route → fall through */ }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        return a.Address.ToString();
            }
        }
        catch { }

        return "127.0.0.1";
    }

    private async Task<ChromecastReceiver?> ResolveAsync(CastConfigStore.CastSpeaker speaker)
    {
        lock (_found)
            if (_found.TryGetValue(speaker.Id, out var hit)) return hit;

        // Not in the last scan — try a fresh one, then fall back to the address
        // we remember (mDNS misses are common; the speaker is usually still there).
        await DiscoverAsync(force: true).ConfigureAwait(false);
        lock (_found)
            if (_found.TryGetValue(speaker.Id, out var hit2)) return hit2;

        if (string.IsNullOrWhiteSpace(speaker.Host)) return null;
        return new ChromecastReceiver
        {
            Name = speaker.Name,
            DeviceUri = new Uri($"http://{speaker.Host}"),
            Port = speaker.Port > 0 ? speaker.Port : 8009
        };
    }

    private static string KeyOf(ChromecastReceiver r)
    {
        if (r.ExtraInformation != null
            && r.ExtraInformation.TryGetValue("id", out var id)
            && !string.IsNullOrWhiteSpace(id))
            return id.Trim();
        var host = r.DeviceUri?.Host ?? "";
        return host.Length == 0 ? "" : $"{host}:{r.Port}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Session[] live;
        lock (_sessions)
        {
            live = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var s in live)
        {
            try { s.Client.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        }
        try { _gate.Dispose(); } catch { }
    }
}
