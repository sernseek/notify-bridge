# Builds the Rust host receiver. `src` defaults to the sibling host/ tree so the
# repo builds standalone; the consuming flake may override it (e.g. with a
# flake input) when this repo is used as a submodule.
{
  lib,
  rustPlatform,
  # Named `source` (not `src`) so callPackage does not auto-fill it from
  # nixpkgs' own `src` attribute.
  source ? ../host,
}:

rustPlatform.buildRustPackage {
  pname = "notify-bridge-host";
  version = "0.1.0";

  src = source;

  cargoLock.lockFile = "${source}/Cargo.lock";

  meta = {
    description = "Receive Windows guest notifications over HTTP and re-emit them on the Linux desktop";
    mainProgram = "notify-bridge-host";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
  };
}
