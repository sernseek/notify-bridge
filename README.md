# notify-bridge

Forward Windows notifications to a Linux host's desktop notification daemon.
Built for a VMware Workstation setup where the **host** runs NixOS + niri
(Noctalia/mako) and the **guest** runs Windows, connected over VMware NAT
(vmnet8) — but nothing is VMware-specific beyond the guest being able to reach
the host over the network.

```
Windows guest                                          Linux host
┌─────────────────────────────┐                ┌──────────────────────────────┐
│ windows-agent (C#/.NET 10)   │                │ host (Rust)                  │
│ UserNotificationListener     │ ──HTTP POST──▶ │ POST /notify  (token-checked)│
│ → captures Action Center     │   {app,title,  │ → org.freedesktop.           │
│ → discovers host, forwards   │     body,...}  │   Notifications (D-Bus)      │
└─────────────────────────────┘                │ → niri / Noctalia popup      │
                                                └──────────────────────────────┘
```

The guest pushes: a C# agent subscribes to the Windows Action Center and POSTs
each new toast to the host. The host is a tiny Rust HTTP receiver that re-emits
it through the freedesktop notification spec.

## Components

| Path | What |
| --- | --- |
| [`host/`](host/) | Rust receiver. Single binary, no runtime deps (`tiny_http` + `notify-rust`). |
| [`windows-agent/`](windows-agent/) | C# .NET 10 agent. Captures toasts, discovers the host, forwards JSON. |
| [`nix/package.nix`](nix/package.nix) | `buildRustPackage` for the host binary. |
| [`nix/home-module.nix`](nix/home-module.nix) | home-manager module: runs the host as a `systemd --user` service. |
| [`Package/AppxManifest.xml`](windows-agent/Package/AppxManifest.xml) | MSIX manifest (package identity → event-driven). |
| [`windows-agent/build-msix.ps1`](windows-agent/build-msix.ps1) | One-shot build → sign → install of the MSIX. |

## Prerequisites

- **Host:** a Linux desktop with a notification daemon implementing
  `org.freedesktop.Notifications` (mako, dunst, Noctalia, GNOME, KDE, …) and a
  running user D-Bus session. Rust toolchain only if you build outside Nix.
- **Guest:** Windows 10/11, the **.NET 10 SDK**, and (for the MSIX build) the
  **Windows 10/11 SDK** (`MakeAppx.exe` + `SignTool.exe`).
- **Network:** the guest must be able to reach the host on the receiver port
  (default `8787/tcp`).

---

## Part 1 — Host (Linux)

The host runs a small HTTP server that turns `POST /notify` into a desktop
notification.

### Quick start (any distro)

```sh
cd host
NOTIFY_BRIDGE_BIND=0.0.0.0:8787 NOTIFY_BRIDGE_TOKEN=$(openssl rand -hex 24) cargo run
```

Open the port for the guest in your firewall, and note the token — the guest
needs the same one.

### NixOS (home-manager)

Consume this repo as a `flake = false` input (submodule contents are invisible to
the flake), then import the module:

```nix
# flake.nix
inputs.notify-bridge-src = { url = "github:sernseek/notify-bridge"; flake = false; };
# pass notify-bridge-src into home-manager.extraSpecialArgs
```

```nix
# a home-manager module
{ pkgs, notify-bridge-src, ... }:
{
  imports = [ "${notify-bridge-src}/nix/home-module.nix" ];
  services.notify-bridge = {
    enable = true;
    package = pkgs.callPackage "${notify-bridge-src}/nix/package.nix" {
      source = "${notify-bridge-src}/host";
    };
    bind = "0.0.0.0:8787";                                   # vmnet8 subnet may change; token guards it
    tokenFile = "/path/to/secret/notify-bridge.env";         # contains NOTIFY_BRIDGE_TOKEN=<secret>
  };
}
```

Open the port **only on the VMware interface** so it is not exposed on
tether/tailscale:

```nix
networking.firewall.interfaces."vmnet8".allowedTCPPorts = [ 8787 ];
```

Create the token file (keep it out of any public repo), then rebuild:

```sh
echo "NOTIFY_BRIDGE_TOKEN=$(openssl rand -hex 24)" > /path/to/secret/notify-bridge.env
chmod 600 /path/to/secret/notify-bridge.env
sudo nixos-rebuild switch --flake /etc/nixos#<host>
```

Verify it is listening and pops a notification:

```sh
systemctl --user status notify-bridge
TOKEN=$(grep -oP 'NOTIFY_BRIDGE_TOKEN=\K.*' /path/to/secret/notify-bridge.env)
curl -X POST -H "X-Bridge-Token: $TOKEN" -H 'Content-Type: application/json' \
  http://127.0.0.1:8787/notify -d '{"app":"test","title":"hello","body":"it works"}'
```

### Host environment variables

| var | default | meaning |
| --- | --- | --- |
| `NOTIFY_BRIDGE_BIND` | `127.0.0.1:8787` | listen address |
| `NOTIFY_BRIDGE_TOKEN` | _(unset)_ | if set, requests must send a matching `X-Bridge-Token` header |
| `NOTIFY_BRIDGE_APP_PREFIX` | `1` | prefix the summary with the source app name |

---

## Part 2 — Guest (Windows)

The agent must run in the **interactive user session** (a session-0 Windows
Service cannot read per-user toasts). Real-time delivery needs **package
identity**, so the recommended install is a signed MSIX. Config and logs:

- config: `%USERPROFILE%\.notify-bridge\config.json`
- log: `%ProgramData%\notify-bridge\agent.log`

### Install (MSIX, event-driven — recommended)

```powershell
git clone https://github.com/sernseek/notify-bridge
cd notify-bridge\windows-agent
.\build-msix.ps1        # publish → pack → self-signed cert → sign → trust → install
                        # self-elevates once (UAC) for cert trust + install
```

Then create the config and launch once:

```powershell
mkdir $env:USERPROFILE\.notify-bridge -Force
@'
{ "endpoint": "auto", "port": 8787, "token": "PASTE_THE_SAME_TOKEN_HERE" }
'@ | Set-Content $env:USERPROFILE\.notify-bridge\config.json
# Launch "Notify Bridge Agent" from the Start menu once to grant the
# notification-access prompt. It then auto-starts at logon (manifest StartupTask).
```

Re-running `build-msix.ps1` after a code change rebuilds and reinstalls; it stops
any running instance first so the publish step is not blocked.

### Install (unpackaged — polling fallback)

Lighter, no SDK/cert, but ~3 s latency (no `NotificationChanged` without identity):

```powershell
cd windows-agent
dotnet publish -c Release -r win-x64 --self-contained false
cd bin\Release\net10.0-windows10.0.19041.0\win-x64\publish
.\NotifyBridgeAgent.exe --install     # per-user HKCU Run autostart, no admin
Start-Process .\NotifyBridgeAgent.exe
```

`--uninstall` removes the Run entry.

### Guest config (`config.json`)

| key | default | meaning |
| --- | --- | --- |
| `endpoint` | `"auto"` | `"auto"` = discover the host by scanning; or `"http://ip:port/notify"` to pin it |
| `port` | `8787` | port used for discovery / the default host port |
| `token` | `""` | must match the host's `NOTIFY_BRIDGE_TOKEN` |
| `backstopSeconds` | `0` | safety re-sync interval; `0` = auto (60 s event-driven, 3 s polling) |

`NOTIFY_BRIDGE_ENDPOINT` / `NOTIFY_BRIDGE_TOKEN` environment variables override
the file.

---

## Verify end-to-end

1. Host: `systemctl --user status notify-bridge` is active and listening.
2. Guest log shows `event-driven: subscribed to NotificationChanged` and, after
   the first notification, `discovered host at http://…/notify` with no `drop`
   line following it:
   ```powershell
   Get-Content $env:ProgramData\notify-bridge\agent.log -Tail 10
   ```
3. Raise a test toast (Windows PowerShell 5.1 — not pwsh 7):
   ```powershell
   powershell.exe -NoProfile -Command "[void][Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]; $x=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); $t=$x.GetElementsByTagName('text'); [void]$t.Item(0).AppendChild($x.CreateTextNode('Test')); [void]$t.Item(1).AppendChild($x.CreateTextNode('Windows to Linux')); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.Shell.RunDialog').Show([Windows.UI.Notifications.ToastNotification]::new($x))"
   ```
   The host desktop should pop "test: Test / Windows to Linux".

## Troubleshooting

| symptom | cause / fix |
| --- | --- |
| guest log: `no reachable host` | host not running, or firewall blocks the port on the guest-facing interface |
| guest log: `host … returned 401` | token mismatch between `config.json` and the host's env file |
| guest log: `NotificationChanged unavailable …` | not installed as MSIX → running in polling mode (still works, slower) |
| nothing pops but host got 204 | host has no notification daemon / no user D-Bus session |
| agent not running after a code change | a running instance locked the publish DLL — `build-msix.ps1` now kills it first |
| toast snippet errors with "Unable to find type" | you used pwsh 7; WinRT type loading needs Windows PowerShell 5.1 (`powershell.exe`) |

## How it works (design notes)

- **Why not bridged networking / why the host IP isn't hardcoded:** a phone
  hotspot or USB tether makes the VM NAT-only and the subnet can vary, so the
  guest *discovers* the host by scanning each up interface's /24 for `GET /health`
  (gateways and `.1/.2` first) instead of relying on a fixed IP.
- **Why a logon task / MSIX, not a Windows Service:** `UserNotificationListener`
  only works in the interactive user session; session-0 services can't read
  per-user toasts.
- **Why MSIX for real-time:** `NotificationChanged` only fires with package
  identity. Unpackaged builds fall back to a short polling loop.
- **Why logs go to `%ProgramData%`:** MSIX redirects per-user writes into the
  package container, where they are hard to find; `%ProgramData%` is not
  redirected.

## Wire format

See [`PROTOCOL.md`](PROTOCOL.md). One JSON object per `POST /notify`.
