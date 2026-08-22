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

"$PS" -NoProfile -ExecutionPolicy Bypass -File "$WIN_PS1" -Backend wsl
