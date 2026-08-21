#!/usr/bin/env bash
# dsh web manager v2.1 - WSL -> Windows mutual bootstrap.
# Ensures the Windows-side dsh web manager is installed and running, then forwards
# the requested action (default: open). Safe to run repeatedly (idempotent).
# Run inside WSL:   bash ~/.dsh-webui/wsl-bootstrap.sh [open|tray|exit|restart]
set -u

ACTION="${1:-open}"
EXE_NAME="dsh-web-manager.exe"
LOCK="$HOME/.dsh-webui/bootstrap.lock"
LOG="$HOME/.dsh-webui/wsl-bootstrap.log"

log() { echo "[$(date '+%F %T')] $*" >> "$LOG"; }
mkdir -p "$HOME/.dsh-webui" || exit 1

# Crude lock: first-come-first-served; other callers exit (the winner completes the job).
if ! mkdir "$LOCK" 2>/dev/null; then
  exit 0
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

WIN_USER="$(cmd.exe /c 'echo %USERNAME%' 2>/dev/null | tr -d '\r')"
[ -n "$WIN_USER" ] || WIN_USER="$(id -un)"
LOCALAPPDATA="/mnt/c/Users/$WIN_USER/AppData/Local"
EXE_WSL="$LOCALAPPDATA/dsh-web-manager/app/$EXE_NAME"

# 1. Already running -> forward the action to the primary instance.
#    (tasklist.exe //FI does not survive WSL interop arg conversion; use Get-Process.
#     Get-Process -Name does not accept the .exe suffix, hence ${EXE_NAME%.exe}.)
PROC_NAME="${EXE_NAME%.exe}"
if [ "$(powershell.exe -NoProfile -Command "if (Get-Process -Name '$PROC_NAME*' -ErrorAction SilentlyContinue) { 'True' } else { 'False' }" 2>/dev/null | tr -d '\r')" = "True" ]; then
  log "manager already running; forwarding action=$ACTION"
  if [ -f "$EXE_WSL" ]; then
    powershell.exe -NoProfile -Command "& '$EXE_WSL' '$ACTION'" 2>/dev/null &
  fi
  exit 0
fi

# 2. Installed but not running -> start it silently (tray stays, no window unless requested).
if [ -f "$EXE_WSL" ]; then
  log "manager installed but not running; starting (action=$ACTION)"
  powershell.exe -NoProfile -Command "Start-Process -WindowStyle Hidden -FilePath '$EXE_WSL' -ArgumentList '$ACTION'" 2>/dev/null
  exit 0
fi

# 3. Not installed -> silent install from the shared bootstrap copy, then start.
INSTALLER_WSL="/mnt/c/Users/$WIN_USER/.dsh-webui/wsl-bootstrap/Install.ps1"
if [ -f "$INSTALLER_WSL" ]; then
  log "manager missing; installing from shared bootstrap copy"
  powershell.exe -NoProfile -Command "Start-Process -WindowStyle Hidden -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','C:\\Users\\$WIN_USER\\.dsh-webui\\wsl-bootstrap\\Install.ps1','-SkipLaunch'" 2>/dev/null
  sleep 8
  if [ -f "$EXE_WSL" ]; then
    powershell.exe -NoProfile -Command "Start-Process -WindowStyle Hidden -FilePath '$EXE_WSL' -ArgumentList '$ACTION'" 2>/dev/null
  fi
  exit 0
fi

log "cannot bootstrap: manager not installed and no installer found"
exit 1
