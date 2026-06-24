using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Y2KMusicServer.Shared;

// Disambiguating aliases — WinForms types live in System.Windows.Forms,
// WPF types in System.Windows.*. Both are available because the csproj
// sets <UseWPF>true</UseWPF> AND <UseWindowsForms>true</UseWindowsForms>.
using WinForms = System.Windows.Forms;
using Drawing  = System.Drawing;

// Type aliases pin the unqualified names Application/MessageBox to the
// WPF versions. With UseWindowsForms enabled the SDK implicitly imports
// System.Windows.Forms, which has its own Application and MessageBox —
// without these aliases every WPF call site is ambiguous.
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;

namespace Y2KMusicServer.Tray;

/// <summary>
/// The tray's whole lifecycle: build the NotifyIcon, build the
/// context menu, poll the service status, and dispatch menu
/// actions. The tray is intentionally a thin client over the
/// service's admin HTTP endpoints — it does not import server
/// code or touch any service state directly.
///
/// Uses the WinForms <see cref="WinForms.NotifyIcon"/> rather
/// than any of the H.NotifyIcon variants because the WinForms
/// API has shipped in the BCL unchanged since .NET 2.0, works
/// fine inside a WPF app once <c>&lt;UseWindowsForms&gt;true</c>
/// is set, and avoids package-compatibility surprises in
/// .NET 8 (the H.NotifyIcon.Wpf 2.4.x package on NuGet at the
/// time of writing only carries .NET Framework targets — NuGet
/// restores it via the compat shim and the resulting type is
/// missing methods like TryForceCreate that the docs claim
/// exist).
/// </summary>
public sealed class TrayApp : IDisposable
{
    private const string ServiceName = "Y2KMusicServer";
    private const string BaseUrl     = "http://localhost:8765";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private WinForms.NotifyIcon? _icon;
    private DispatcherTimer? _poll;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource _cts = new();

    // Menu items held as fields so UpdateUi() can mutate their labels
    // and enabled state without rebuilding the menu.
    private WinForms.ToolStripMenuItem? _statusItem;
    private WinForms.ToolStripMenuItem? _startItem;
    private WinForms.ToolStripMenuItem? _stopItem;
    private WinForms.ToolStripMenuItem? _restartItem;
    private WinForms.ToolStripMenuItem? _updateItem;

    private ServiceStatusDto? _lastStatus;
    private bool _running;

    public void Initialise()
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Y2K Music Server (starting…)",
            ContextMenuStrip = BuildMenu(),
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenAdmin();
        };
        Debug.WriteLine("[Tray] NotifyIcon created, Visible=true");

        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _poll.Start();

        // First poll immediately so the menu is correct from second 0.
        _ = RefreshAsync();
    }

    /// <summary>
    /// Loads the .ico from the WPF resource stream. The icon is
    /// declared as a &lt;Resource&gt; in the csproj, which means it's
    /// embedded in the assembly and accessible via the pack URI.
    /// Falls back to the system "application" icon if anything goes
    /// wrong, so the tray always has *something* visible.
    /// </summary>
    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/tray.ico");
            var info = Application.GetResourceStream(uri);
            if (info != null)
            {
                using var s = info.Stream;
                var icon = new Drawing.Icon(s);
                Debug.WriteLine("[Tray] Icon loaded from " + uri);
                return icon;
            }
            Debug.WriteLine("[Tray] GetResourceStream returned null for " + uri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Tray] Icon load failed: " + ex);
        }
        return Drawing.SystemIcons.Application;
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var openAdmin = new WinForms.ToolStripMenuItem("Open admin page");
        openAdmin.Click += (_, _) => OpenAdmin();
        menu.Items.Add(openAdmin);

        var openListener = new WinForms.ToolStripMenuItem("Open listener page");
        openListener.Click += (_, _) => OpenUrl(BaseUrl + "/");
        menu.Items.Add(openListener);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        _statusItem = new WinForms.ToolStripMenuItem("Service: (unknown)") { Enabled = false };
        menu.Items.Add(_statusItem);

        _startItem = new WinForms.ToolStripMenuItem("Start service");
        _startItem.Click += async (_, _) => await StartServiceAsync();
        menu.Items.Add(_startItem);

        _stopItem = new WinForms.ToolStripMenuItem("Stop service");
        _stopItem.Click += async (_, _) => await StopServiceAsync();
        menu.Items.Add(_stopItem);

        _restartItem = new WinForms.ToolStripMenuItem("Restart service");
        _restartItem.Click += async (_, _) => await RestartServiceAsync();
        menu.Items.Add(_restartItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        _updateItem = new WinForms.ToolStripMenuItem("Check for updates");
        _updateItem.Click += async (_, _) => await CheckForUpdatesAsync(interactive: true);
        menu.Items.Add(_updateItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var quit = new WinForms.ToolStripMenuItem("Quit tray");
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    private async Task RefreshAsync()
    {
        try
        {
            _lastStatus = await _http.GetFromJsonAsync<ServiceStatusDto>(
                BaseUrl + "/api/admin/service/status", _cts.Token);
            _running = _lastStatus is not null;
        }
        catch
        {
            _lastStatus = null;
            _running = false;
        }
        UpdateUi();
    }

    private void UpdateUi()
    {
        if (_icon is null) return;

        // NotifyIcon.Text is capped at 127 chars in modern Windows but
        // we keep tooltips well short of that anyway.
        if (_running && _lastStatus is not null)
        {
            var u = _lastStatus.Update;
            var updTail = (u?.Available == true)
                ? $"  •  Update {u.LatestVersion} available"
                : "";
            _icon.Text = Truncate($"Y2K Music Server  v{_lastStatus.Version}{updTail}", 127);
        }
        else
        {
            _icon.Text = "Y2K Music Server (service not responding)";
        }

        if (_running && _lastStatus is not null)
        {
            var u = _lastStatus.Update;
            if (_statusItem is not null)
                _statusItem.Text = (u?.Available == true)
                    ? $"Update available: v{u.LatestVersion}"
                    : $"Service: running  (v{_lastStatus.Version})";
        }
        else if (_statusItem is not null)
        {
            _statusItem.Text = "Service: not running";
        }

        if (_startItem   is not null) _startItem.Enabled   = !_running;
        if (_stopItem    is not null) _stopItem.Enabled    = _running;
        if (_restartItem is not null) _restartItem.Enabled = _running;

        if (_updateItem is not null)
        {
            _updateItem.Text = (_lastStatus?.Update?.Available == true)
                ? $"Install update v{_lastStatus.Update.LatestVersion}…"
                : "Check for updates";
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    // ── Actions ─────────────────────────────────────────────────────

    private void OpenAdmin()
    {
        var url = _lastStatus?.AdminUrl ?? (BaseUrl + "/admin");
        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open URL: " + ex.Message, "Y2K");
        }
    }

    private async Task StartServiceAsync() => await ControlService(c => c.Start());
    private async Task StopServiceAsync()  => await ControlService(c => c.Stop());
    private async Task RestartServiceAsync()
    {
        await ControlService(c =>
        {
            if (c.Status == ServiceControllerStatus.Running) c.Stop();
            c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            c.Start();
        });
    }

    private async Task ControlService(Action<ServiceController> action)
    {
        await Task.Run(() =>
        {
            try
            {
                using var c = new ServiceController(ServiceName);
                action(c);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    MessageBox.Show(ex.Message,
                        "Service control failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        });
        await RefreshAsync();
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        UpdateInfoDto? info = null;
        try
        {
            var resp = await _http.PostAsync(BaseUrl + "/api/admin/service/update/check", null, _cts.Token);
            info = await resp.Content.ReadFromJsonAsync<UpdateInfoDto>(cancellationToken: _cts.Token);
        }
        catch (Exception ex)
        {
            if (interactive)
                MessageBox.Show("Update check failed: " + ex.Message, "Y2K",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RefreshAsync();

        if (info?.Available == true)
        {
            if (interactive)
            {
                var ans = MessageBox.Show(
                    $"Update available: v{info.LatestVersion}\n" +
                    $"Current: v{info.CurrentVersion}\n\n" +
                    "Download and install now?",
                    "Y2K Music Server",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ans == MessageBoxResult.Yes)
                    await DownloadAndRunInstallerAsync(info);
            }
        }
        else if (interactive)
        {
            MessageBox.Show(
                info?.CheckError is { Length: > 0 } e
                    ? "Update check failed: " + e
                    : $"You're up to date. (v{info?.CurrentVersion})",
                "Y2K Music Server",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task DownloadAndRunInstallerAsync(UpdateInfoDto info)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            MessageBox.Show("Release has no .zip asset to install.", "Y2K");
            return;
        }
        var tempZip = Path.Combine(Path.GetTempPath(),
            $"Y2K-update-{info.LatestVersion}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"Y2K-update-{info.LatestVersion}");

        try
        {
            using (var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var resp = await dl.GetAsync(info.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead, _cts.Token))
            using (var fs = File.Create(tempZip))
            {
                resp.EnsureSuccessStatusCode();
                await resp.Content.CopyToAsync(fs, _cts.Token);
            }

            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir);

            var installScript = Path.Combine(tempDir, "installer", "install.ps1");
            if (!File.Exists(installScript))
            {
                MessageBox.Show("Update zip is missing installer\\install.ps1.", "Y2K");
                return;
            }

            // Launch PowerShell elevated to run the new installer. The
            // tray exits — the new install.ps1 will restart the service
            // and the tray (HKLM Run entry takes care of next sign-in;
            // we also launch the tray ourselves at the end of install).
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installScript}\"",
                UseShellExecute = true,
                Verb = "runas"
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Update download/install failed: " + ex.Message, "Y2K",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _poll?.Stop();
        if (_icon is not null)
        {
            _icon.Visible = false;   // remove from tray immediately
            _icon.Dispose();
        }
        _http.Dispose();
        _cts.Dispose();
    }
}
