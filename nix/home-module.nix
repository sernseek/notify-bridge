# Reusable home-manager module that runs the notify-bridge host receiver as a
# systemd --user service. Import it and set `services.notify-bridge.*`.
{
  config,
  lib,
  pkgs,
  ...
}:

let
  cfg = config.services.notify-bridge;
in
{
  options.services.notify-bridge = {
    enable = lib.mkEnableOption "notify-bridge host receiver";

    package = lib.mkOption {
      type = lib.types.package;
      description = "The notify-bridge-host package to run.";
    };

    bind = lib.mkOption {
      type = lib.types.str;
      default = "127.0.0.1:8787";
      example = "172.16.121.1:8787";
      description = ''
        Listen address. On a VMware host, bind the vmnet8 gateway IP so only
        NAT guests can reach it.
      '';
    };

    appPrefix = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Prefix the notification summary with the source app name.";
    };

    tokenFile = lib.mkOption {
      type = lib.types.nullOr lib.types.path;
      default = null;
      example = "/etc/nixos/nixos-secrets/notify-bridge.env";
      description = ''
        Optional EnvironmentFile providing NOTIFY_BRIDGE_TOKEN=<secret>. When set,
        clients must send a matching X-Bridge-Token header.
      '';
    };

    wantedBy = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ "niri.service" ];
      description = "Units that should pull in the notify-bridge service.";
    };
  };

  config = lib.mkIf cfg.enable {
    systemd.user.services.notify-bridge = {
      Unit = {
        Description = "notify-bridge: receive Windows guest notifications";
        PartOf = [ "graphical-session.target" ];
        After = [ "graphical-session.target" ];
      };
      Install.WantedBy = cfg.wantedBy;
      Service = {
        Type = "simple";
        Environment = [
          "NOTIFY_BRIDGE_BIND=${cfg.bind}"
          "NOTIFY_BRIDGE_APP_PREFIX=${if cfg.appPrefix then "1" else "0"}"
        ]
        ++ lib.optional (cfg.tokenFile == null) "NOTIFY_BRIDGE_TOKEN=";
        EnvironmentFile = lib.mkIf (cfg.tokenFile != null) (toString cfg.tokenFile);
        ExecStart = lib.getExe cfg.package;
        Restart = "always";
        # The bind IP only exists while VMware's vmnet8 is up, so back off a bit
        # to avoid tight restart churn when VMware isn't running.
        RestartSec = 10;
      };
    };
  };
}
