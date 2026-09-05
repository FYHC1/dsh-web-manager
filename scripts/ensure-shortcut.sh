#!/usr/bin/env bash
# dsh-web-manager (WSL side): create the Windows-desktop "(wsl)" shortcut that
# launches the shared tray manager with "open wsl". Delegates to
# ensure-shortcut.ps1 via Windows PowerShell (interop), after registering WSL
# interop if the distro disabled it (systemd=true sometimes drops it).
set -euo pipefail

# Restore WSL interop if missing (Windows EXEs would otherwise "Exec format error").
if grep -qi microsoft /proc/version 2>/dev/null && [ ! -e /proc/sys/fs/binfmt_misc/WSLInterop ] 2>/dev/null; then
  echo ':WSLInterop:M::MZ::/init:PF' | sudo -n tee /proc/sys/fs/binfmt_misc/register >/dev/null 2>&1 || true
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PS=""
if [ -f /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe ]; then
  PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
else
  PS="$(command -v powershell.exe || true)"
fi
[ -n "$PS" ] || { echo "[ensure-shortcut] powershell.exe not found (WSL interop unavailable)" >&2; exit 1; }

# Convert the WSL path to a Windows-accessible one (\\wsl.localhost\<distro>\...).
WIN_PS1="$(wslpath -w "$SCRIPT_DIR/ensure-shortcut.ps1" 2>/dev/null || echo "$SCRIPT_DIR/ensure-shortcut.ps1")"

# Copy the script to the WINDOWS temp dir and execute it from there when
# possible: PowerShell 5.1 reading a .ps1 straight off \wsl.localhost (9P) is
# unreliable - truncated reads surface as nonsense parse errors. A local
# %TEMP% copy sidesteps 9P entirely.
TMP_WIN_PS1="$WIN_PS1"
WIN_TEMP="$("$PS" -NoProfile -Command '$env:TEMP' 2>/dev/null | tr -d '')"
if [ -n "$WIN_TEMP" ]; then
  U_WIN_TEMP="$(wslpath -u "$WIN_TEMP" 2>/dev/null || true)"
  if [ -n "$U_WIN_TEMP" ] && cp "$SCRIPT_DIR/ensure-shortcut.ps1" "$U_WIN_TEMP/ensure-shortcut-wsl.ps1" 2>/dev/null; then
    TMP_WIN_PS1="$(wslpath -w "$U_WIN_TEMP/ensure-shortcut-wsl.ps1" 2>/dev/null || echo "$WIN_PS1")"
  fi
fi

"$PS" -NoProfile -ExecutionPolicy Bypass -File "$TMP_WIN_PS1" -Backend wsl

# Register the dsh-webui command: open the standalone window from WSL via the
# shared tray manager (idempotent; refreshed on every plugin update).
if [ -f "$SCRIPT_DIR/dsh-webui" ]; then
  mkdir -p "$HOME/.local/bin" 2>/dev/null || true
  if install -m 755 "$SCRIPT_DIR/dsh-webui" "$HOME/.local/bin/dsh-webui" 2>/dev/null; then
    echo "[ensure-shortcut] registered ~/.local/bin/dsh-webui"
  else
    echo "[ensure-shortcut] could not register ~/.local/bin/dsh-webui" >&2
  fi
fi
