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

Requires the .NET 10 SDK. Built as a `WinExe` (no console window at logon).

```powershell
cd windows-agent
dotnet publish -c Release -r win-x64 --self-contained false
# copy config.example.json -> config.json next to the published exe, set the token
cd bin\Release\net10.0-windows10.0.19041.0\win-x64\publish
.\NotifyBridgeAgent.exe --install     # self-registers a hidden logon task (schtasks)
schtasks /Run /TN NotifyBridgeAgent   # start now without logging out
```

`--uninstall` removes the task. First launch triggers a one-time Windows
permission prompt for notification access (Settings → Privacy & security →
Notifications). Grant it once.

### Why a logon task and not a Windows Service

`UserNotificationListener` only works in the interactive user session. A
session-0 Windows Service cannot read per-user toasts, so the agent must run as
the logged-in user — hence a logon scheduled task, which the exe registers
itself (no PowerShell needed).

### Host discovery

With `"endpoint": "auto"` (the default) the agent scans every up IPv4
interface's local /24 for a host answering `GET /health` on `port`, preferring
gateways and `.1/.2`. The first responder is cached and reused; on a send
failure it rescans. This means the VMware NAT subnet can change without editing
any IP. Set an explicit `"endpoint": "http://ip:port/notify"` to skip discovery.

Config is read from `config.json` next to the exe, overridable by the
`NOTIFY_BRIDGE_ENDPOINT` / `NOTIFY_BRIDGE_TOKEN` environment variables. A log is
written to `%LOCALAPPDATA%\notify-bridge\agent.log`.
