// notify-bridge Windows agent.
//
// Subscribes to the Action Center via the UserNotificationListener WinRT API,
// and forwards each newly added toast to the Linux host receiver as JSON.

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

internal static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly HashSet<uint> Seen = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Config _cfg = new();

    private static async Task<int> Main()
    {
        _cfg = Config.Load();
        Log($"endpoint={_cfg.Endpoint} token={(string.IsNullOrEmpty(_cfg.Token) ? "no" : "yes")}");

        UserNotificationListener listener = UserNotificationListener.Current;
        UserNotificationListenerAccessStatus access = await listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            Log($"notification access not granted: {access}");
            Log("grant it under Settings -> Privacy & security -> Notifications, then rerun.");
            return 1;
        }

        // Prime the seen-set with whatever is already in the Action Center so we
        // don't replay the backlog on startup.
        await Refresh(listener, forward: false);
        Log("primed; watching for new notifications");

        // Event-driven push. NotificationChanged can be flaky, so we also poll.
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
                    continue; // already processed
                }
                if (forward)
                {
                    await Forward(n);
                }
            }

            // Drop ids that are no longer present, so a dismissed-then-reshown
            // notification with the same id can fire again.
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
            return; // nothing worth showing
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
        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Endpoint)
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
            if (!resp.IsSuccessStatusCode)
            {
                Log($"host returned {(int)resp.StatusCode} for [{app}] {title}");
            }
        }
        catch (Exception ex)
        {
            Log($"forward failed for [{app}] {title}: {ex.Message}");
        }
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

    private static void Log(string msg) =>
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] notify-bridge: {msg}");
}

internal sealed class Config
{
    public string Endpoint { get; set; } = "http://172.16.121.1:8787/notify";
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
