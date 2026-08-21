#!/usr/bin/env bash
# dsh web manager v3.0 - systemd ExecStart wrapper (foreground).
# systemd tracks the dsh process directly and Restart=on-failure heals it;
# logs go to journald (journalctl --user -u dsh-web-<port>).
# Usage: wsl-systemd-start.sh <profile> <port>
# NOTE: this file is mirrored in src/WslTools.cs (WslSystemdStartScript). Keep them in sync.
set -u

PROFILE="${1:-web}"
PORT="${2:-3080}"
BRIDGE_PORT="${3:-0}"
BRIDGE_TOKEN="${4:-}"
HOST="127.0.0.1"

# --- toolchain bootstrap (best effort, same as wsl-start.sh) ---
if ! command -v dsh >/dev/null 2>&1; then
  export PATH="$HOME/.local/bin:$HOME/bin:$PATH"
  if command -v fnm >/dev/null 2>&1; then
    eval "$(fnm env --use-on-cd 2>/dev/null)" || true
  fi
  if ! command -v dsh >/dev/null 2>&1; then
    FNM_ROOT="$HOME/.local/share/fnm/node-versions"
    LATEST="$(ls -1 "$FNM_ROOT" 2>/dev/null | sort -V | tail -1)"
    [ -n "$LATEST" ] && export PATH="$FNM_ROOT/$LATEST/installation/bin:$PATH"
  fi
fi
if ! command -v dsh >/dev/null 2>&1; then
  echo "ERROR: dsh not found in distro" >&2
  exit 2
fi

export DSH_BRIDGE_PORT="$BRIDGE_PORT"
export DSH_BRIDGE_TOKEN="$BRIDGE_TOKEN"
export DSH_PROFILE="$PROFILE"
export DSH_WEB_PORT="$PORT"
export DSH_WEB_HOST="$HOST"
exec dsh --profile "$PROFILE" --host "$HOST" --port "$PORT"
