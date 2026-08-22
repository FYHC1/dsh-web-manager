#!/usr/bin/env bash
# dsh web manager v2.2 - WSL-side dsh web launcher (first-line self-heal).
# Usage: wsl-start.sh <profile> <port>
# dsh intentionally rejects --host 0.0.0.0 (RCE safety), so the service binds
# 127.0.0.1 inside WSL; Windows reaches it through localhost forwarding.
# NOTE: this file is mirrored in src/WslTools.cs (WslStartScript). Keep them in sync.
set -u

PROFILE="${1:-web}"
PORT="${2:-3080}"
BRIDGE_PORT="${3:-0}"
BRIDGE_TOKEN="${4:-}"
HOST="127.0.0.1"
DWM_DIR="$HOME/.dsh-webui"
PIDFILE="$DWM_DIR/wsl-dsh-$PORT.pid"
LOG="$DWM_DIR/wsl-dsh.log"
mkdir -p "$DWM_DIR" || exit 1

# --- toolchain bootstrap (best effort) ---
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

log() { echo "[$(date '+%F %T')] $*" >> "$LOG"; }

DSH_PID=0
cleanup() {
  log "received TERM, stopping dsh pid=$DSH_PID"
  if [ "$DSH_PID" -gt 0 ]; then
    kill -TERM "$DSH_PID" 2>/dev/null
    wait "$DSH_PID" 2>/dev/null
  fi
  rm -f "$PIDFILE"
  exit 0
}
trap cleanup TERM INT

# Interruptible sleep: bash defers a trap until the current foreground command
# completes, so `sleep 60` would delay TERM handling by up to 60 s. Running sleep
# in the background and `wait`-ing makes the trap fire immediately.
sleep_int() { sleep "$1" & wait "$!" 2>/dev/null; }

log "wsl-start.sh starting profile=$PROFILE port=$PORT (pid=$$)"

if ! command -v dsh >/dev/null 2>&1; then
  log "ERROR: dsh not found in distro"
  exit 2
fi

CRASHES=0
while true; do
  log "launching dsh --profile $PROFILE --host $HOST --port $PORT (bridge=$BRIDGE_PORT)"
  DSH_BRIDGE_PORT="$BRIDGE_PORT" DSH_BRIDGE_TOKEN="$BRIDGE_TOKEN" \
  DSH_PROFILE="$PROFILE" DSH_WEB_PORT="$PORT" DSH_WEB_HOST="$HOST" \
  dsh --profile "$PROFILE" --host "$HOST" --port "$PORT" >> "$LOG" 2>&1 &
  DSH_PID=$!
  echo "$DSH_PID" > "$PIDFILE"
  wait "$DSH_PID"
  CODE=$?
  DSH_PID=0
  log "dsh exited code=$CODE"
  if [ "$CODE" -ne 0 ]; then
    CRASHES=$((CRASHES+1))
    if [ "$CRASHES" -ge 10 ]; then
      log "10 consecutive failures, sleeping 60s"
      sleep_int 60
      CRASHES=0
    else
      sleep_int 3
    fi
  else
    CRASHES=0
    sleep_int 2
  fi
done
