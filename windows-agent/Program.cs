// notify-bridge Windows agent.
//
// Subscribes to the Action Center via the UserNotificationListener WinRT API and
// forwards each newly added toast to the Linux host receiver as JSON.
//
// Usage:
//   NotifyBridgeAgent.exe              run the agent (normal mode)
//   NotifyBridgeAgent.exe --install    register a hidden logon task (self, no PS)
//   NotifyBridgeAgent.exe --uninstall  remove the logon task
//
// Must run in the interactive user session: UserNotificationListener cannot read
// per-user toasts from a session-0 Windows Service, which is why this self-starts
// via a logon scheduled task rather than as a service.

using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

internal static class Program
{
    private const string RunValueName = "NotifyBridgeAgent";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly HashSet<uint> Seen = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Config _cfg = new();

    // Resolved "http://ip:port/notify" once discovery (or explicit config) succeeds.
    private static string? _endpoint;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            string cmd = args[0].TrimStart('-').ToLowerInvariant();
            if (cmd is "install")
            {
                return Install();
            }
            if (cmd is "uninstall")
            {
                return Uninstall();
            }
        }

        _cfg = Config.Load();
        Log($"start: configEndpoint='{_cfg.Endpoint}' port={_cfg.Port} token={(string.IsNullOrEmpty(_cfg.Token) ? "no" : "yes")}");

        UserNotificationListener listener = UserNotificationListener.Current;
        UserNotificationListenerAccessStatus access = await listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            Log($"notification access not granted: {access}. Allow it under " +
                "Settings -> Privacy & security -> Notifications, then rerun.");
            return 1;
        }

        // Prime the seen-set so we don't replay the existing backlog on startup.
        await Refresh(listener, forward: false);
        Log("primed; watching for new notifications");

        listener.NotificationChanged += async (_, _) =>
        {
            try { await Refresh(listener, forward: true); }
            catch (Exception ex) { Log($"event refresh failed: {ex.Message}"); }
        };

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { await Refresh(listener, forward: true); }
            catch (Exception ex) { Log($"poll refresh failed: {ex.Message}"); }
        }
    }

    private static async Task Refresh(UserNotificationListener listener, bool forward)
    {
        await Gate.WaitAsync();
        try
        {
            IReadOnlyList<UserNotification> notes =
                await listener.GetNotificationsAsync(NotificationKinds.Toast);

            var current = new HashSet<uint>();
            foreach (UserNotification n in notes)
            {
                current.Add(n.Id);
                if (!Seen.Add(n.Id))
                {
                    continue;
                }
                if (forward)
                {
                    await Forward(n);
                }
            }

            // Drop ids no longer present so a re-shown id can fire again.
            Seen.IntersectWith(current);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task Forward(UserNotification n)
    {
        string app = SafeAppName(n);
        string title = "";
        var bodyBuilder = new StringBuilder();

        NotificationBinding? binding =
            n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding != null)
        {
            IReadOnlyList<AdaptiveNotificationText> texts = binding.GetTextElements();
            bool first = true;
            foreach (AdaptiveNotificationText t in texts)
            {
                if (first)
                {
                    title = t.Text;
                    first = false;
                }
                else
                {
                    if (bodyBuilder.Length > 0)
                    {
                        bodyBuilder.Append('\n');
                    }
                    bodyBuilder.Append(t.Text);
                }
            }
        }

        string body = bodyBuilder.ToString();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        var payload = new
        {
            id = n.Id.ToString(),
            app,
            title,
            body,
            timestamp = n.CreationTime.ToString("o"),
        };
        string json = JsonSerializer.Serialize(payload);

        // Try the cached endpoint; if that fails, (re)discover and retry once.
        if (await TrySend(json))
        {
            return;
        }
        _endpoint = null;
        if (await EnsureEndpoint() && await TrySend(json))
        {
            return;
        }
        Log($"drop [{app}] {title}: no reachable host");
    }

    private static async Task<bool> TrySend(string json)
    {
        if (string.IsNullOrEmpty(_endpoint) && !await EnsureEndpoint())
        {
            return false;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_cfg.Token))
        {
            req.Headers.Add("X-Bridge-Token", _cfg.Token);
        }

        try
        {
            using HttpResponseMessage resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                return true;
            }
            Log($"host {_endpoint} returned {(int)resp.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"send to {_endpoint} failed: {ex.Message}");
            return false;
        }
    }

    /// Resolve _endpoint from explicit config, or by scanning local subnets.
    private static async Task<bool> EnsureEndpoint()
    {
        if (!string.IsNullOrEmpty(_endpoint))
        {
            return true;
        }

        string cfgEndpoint = _cfg.Endpoint?.Trim() ?? "";
        if (cfgEndpoint.Length > 0 && !cfgEndpoint.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _endpoint = cfgEndpoint;
            return true;
        }

        string? ip = await Discover(_cfg.Port);
        if (ip == null)
        {
            return false;
        }
        _endpoint = $"http://{ip}:{_cfg.Port}/notify";
        Log($"discovered host at {_endpoint}");
        return true;
    }

    /// Scan every up IPv4 interface's /24 for a host answering GET /health.
    /// Gateways and .1/.2 are probed first since the VMware host lives there.
    private static async Task<string?> Discover(int port)
    {
        var priority = new List<string>();
        var rest = new List<string>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties props = nic.GetIPProperties();
            foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    priority.Add(gw.Address.ToString());
                }
            }

            foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                byte[] addr = ua.Address.GetAddressBytes();
                // Bound the scan to the local /24 (254 hosts) regardless of mask.
                for (int host = 1; host <= 254; host++)
                {
                    var b = new byte[] { addr[0], addr[1], addr[2], (byte)host };
                    string cand = new IPAddress(b).ToString();
                    if (host is 1 or 2)
                    {
                        priority.Add(cand);
                    }
                    else if (host != addr[3])
                    {
                        rest.Add(cand);
                    }
                }
            }
        }

        // Probe priority candidates first, then the rest.
        foreach (var batch in new[] { priority, rest })
        {
            string? hit = await FirstResponder(batch.Distinct(), port);
            if (hit != null)
            {
                return hit;
            }
        }
        return null;
    }

    /// Returns the first IP whose /health responds 200, scanning in parallel.
    private static async Task<string?> FirstResponder(IEnumerable<string> ips, int port)
    {
        // Not disposed on purpose: background probe tasks may still touch these
        // after the first responder is found; let the GC reclaim them.
        var cts = new CancellationTokenSource();
        var gate = new SemaphoreSlim(64);
        var tcs = new TaskCompletionSource<string?>();

        var tasks = new List<Task>();
        foreach (string ip in ips)
        {
            tasks.Add(Probe(ip));
        }

        async Task Probe(string ip)
        {
            try
            {
                await gate.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                probeCts.CancelAfter(TimeSpan.FromMilliseconds(400));
                using var resp = await Http.GetAsync(
                    $"http://{ip}:{port}/health", probeCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    tcs.TrySetResult(ip);
                    cts.Cancel();
                }
            }
            catch
            {
                // unreachable / timeout / cancelled — ignore
            }
            finally
            {
                gate.Release();
            }
        }

        _ = Task.WhenAll(tasks).ContinueWith(_ => tcs.TrySetResult(null),
            TaskScheduler.Default);
        return await tcs.Task;
    }

    private static string SafeAppName(UserNotification n)
    {
        try
        {
            string name = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            return string.IsNullOrWhiteSpace(name) ? "Windows" : name;
        }
        catch
        {
            return "Windows";
        }
    }

    // ---- self-registration (no PowerShell) -------------------------------

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    // Autostart via the per-user HKCU Run key: no admin rights, runs in the
    // interactive session at logon, which is exactly what UserNotificationListener
    // needs. (A scheduled task in the root folder would require elevation.)
    private static int Install()
    {
        AttachConsole(AttachParentProcess);
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine("could not determine own exe path");
            return 1;
        }
        try
        {
            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunValueName, $"\"{exe}\"");
            Console.WriteLine($"installed: starts at logon (HKCU Run '{RunValueName}').");
            Console.WriteLine($"start now without logging out:  Start-Process \"{exe}\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"install failed: {ex.Message}");
            return 1;
        }
    }

    private static int Uninstall()
    {
        AttachConsole(AttachParentProcess);
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            Console.WriteLine("uninstalled: removed logon entry.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"uninstall failed: {ex.Message}");
            return 1;
        }
    }

    // ---- logging ----------------------------------------------------------

    private static readonly string LogPath = InitLogPath();

    private static string InitLogPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "notify-bridge");
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        return Path.Combine(dir, "agent.log");
    }

    private static void Log(string msg)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
        try
        {
            // Trim if the log grew past ~1 MiB.
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 1024 * 1024)
            {
                File.WriteAllText(LogPath, "");
            }
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // ignore logging failures
        }
        Console.Error.WriteLine(line); // harmless when no console is attached
    }
}

internal sealed class Config
{
    /// "auto" or "" → discover by scanning; or an explicit
    /// "http://ip:port/notify" to skip discovery.
    public string Endpoint { get; set; } = "auto";

    /// Port used for discovery and as the default host port.
    public int Port { get; set; } = 8787;

    public string Token { get; set; } = "";

    public static Config Load()
    {
        Config cfg = new();
        string path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<Config>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Config();
            }
            catch
            {
                // fall back to defaults / env below
            }
        }

        string? endpoint = Environment.GetEnvironmentVariable("NOTIFY_BRIDGE_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint))
        {
            cfg.Endpoint = endpoint;
        }
        string? token = Environment.GetEnvironmentVariable("NOTIFY_BRIDGE_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            cfg.Token = token;
        }
        return cfg;
    }
}
