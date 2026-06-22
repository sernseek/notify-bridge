# notify-bridge

Forward Windows guest notifications to a Linux host's desktop notification
daemon. Built for a VMware Workstation setup where the host runs NixOS + niri
(Noctalia/mako) and the guest runs Windows, connected over VMware NAT (vmnet8).

```
Windows guest                                   Linux host (vmnet8 gateway side)
[UserNotificationListener]  --HTTP POST-->  [host receiver]  --D-Bus-->  notify daemon
   windows-agent (C#)                         host (Rust)               niri / Noctalia
```

The guest pushes: a C# agent subscribes to the Windows Action Center via the
`UserNotificationListener` WinRT API and POSTs each new toast to the host. The
host runs a tiny Rust HTTP receiver that re-emits it through the freedesktop
notification spec (`org.freedesktop.Notifications`).

## Components

- [`host/`](host/) — Rust receiver. Single binary, no runtime deps. Listens for
  `POST /notify`, validates an optional shared token, emits a desktop
  notification via `notify-rust` (zbus under the hood).
- [`windows-agent/`](windows-agent/) — C# (.NET 8, `net8.0-windows`) agent.
  Captures Action Center toasts and forwards them as JSON.
- [`nix/home-module.nix`](nix/home-module.nix) — home-manager module that builds
  the host binary and runs it as a `systemd --user` service wanted by
  `niri.service`.

## Wire format

See [`PROTOCOL.md`](PROTOCOL.md). One JSON object per `POST /notify`.

## Host (Linux)

```sh
cd host
cargo run    # binds 127.0.0.1:8787 by default; set NOTIFY_BRIDGE_BIND to change
```

Environment:

| var | default | meaning |
| --- | --- | --- |
| `NOTIFY_BRIDGE_BIND` | `127.0.0.1:8787` | listen address; on the VMware host use the vmnet8 gateway IP, e.g. `172.16.121.1:8787` |
| `NOTIFY_BRIDGE_TOKEN` | _(unset)_ | if set, requests must send a matching `X-Bridge-Token` header |
| `NOTIFY_BRIDGE_APP_PREFIX` | `1` | prefix the notification summary with the source app name |

On NixOS this is normally deployed via the home-manager module, not run by hand.

## Windows agent

Requires the .NET 10 SDK and (for the MSIX build) the Windows 10/11 SDK
(`MakeAppx.exe` + `SignTool.exe`). Built as a `WinExe` (no console window).

Config and log live in `%USERPROFILE%\.notify-bridge\` (`config.json`,
`agent.log`) — a stable path that MSIX does not redirect.

### Recommended: MSIX (event-driven, no polling)

`UserNotificationListener.NotificationChanged` only fires when the app has
package identity, so for real-time delivery the agent is installed as a signed
MSIX. `build-msix.ps1` does everything (publish → pack → self-signed cert → sign
→ trust → install) and self-elevates for the trust/install steps:

```powershell
cd windows-agent
.\build-msix.ps1
```

Then put `config.json` in `%USERPROFILE%\.notify-bridge\` (copy
`config.example.json`, set the `token`), and launch **Notify Bridge Agent** once
from the Start menu to grant the one-time notification-access prompt. It then
auto-starts at logon via the manifest's `StartupTask`.

### Alternative: unpackaged (polling fallback)

Without package identity the `NotificationChanged` subscription is unavailable,
so the agent falls back to a short backstop poll. Lighter to deploy, ~3 s latency:

```powershell
cd windows-agent
dotnet publish -c Release -r win-x64 --self-contained false
cd bin\Release\net10.0-windows10.0.19041.0\win-x64\publish
.\NotifyBridgeAgent.exe --install        # per-user HKCU Run autostart, no admin
Start-Process .\NotifyBridgeAgent.exe
```

`--uninstall` removes the Run entry. A session-0 Windows Service is not an option
either way: it cannot read per-user toasts.

### Host discovery

With `"endpoint": "auto"` (the default) the agent scans every up IPv4
interface's local /24 for a host answering `GET /health` on `port`, preferring
gateways and `.1/.2`. The first responder is cached and reused; on a send
failure it rescans. This means the VMware NAT subnet can change without editing
any IP. Set an explicit `"endpoint": "http://ip:port/notify"` to skip discovery.

`backstopSeconds` overrides the safety re-sync interval (0 = auto: 60 s when
event-driven, 3 s when polling). Config values are overridable by the
`NOTIFY_BRIDGE_ENDPOINT` / `NOTIFY_BRIDGE_TOKEN` environment variables.
