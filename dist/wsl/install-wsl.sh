#!/usr/bin/env bash
# ============================================================
#  dsh-offline-bundle WSL-side installer. Runs INSIDE a WSL
#  distro (as the target user, or as root with --user).
#
#  Installs from a payload directory (node/ dsh/ profile-web/
#  wsl-scripts/) produced by scripts/Build-Bundle-Wsl.sh:
#    1. portable tree -> <prefix> (copied unless --skip-tree)
#    2. dsh shim      -> <prefix>/bin/dsh (+ ~/.local/bin/dsh)
#    3. profile       -> ~/.dsh (fill gaps, never touch credentials)
#    4. companion scripts -> ~/.dsh-webui/
#    5. optional offline-start gate (--gate-port)
#
#  Usage (user mode):
#    install-wsl.sh --src /mnt/c/.../bundle/wsl --prefix ~/.dsh-bundle
#  Usage (deb postinst, payload already at /opt):
#    install-wsl.sh --src /opt/dsh-bundle --prefix /opt/dsh-bundle \
#                   --skip-tree --user hgl
# ============================================================
set -euo pipefail

SRC=""
PREFIX=""
SKIP_TREE=0
SKIP_PROFILE=0
SKIP_SCRIPTS=0
GATE_PORT=""
AS_USER=""

while [ $# -gt 0 ]; do
  case "$1" in
    --src) SRC="$2"; shift 2 ;;
    --prefix) PREFIX="$2"; shift 2 ;;
    --skip-tree) SKIP_TREE=1; shift ;;
    --skip-profile) SKIP_PROFILE=1; shift ;;
    --skip-scripts) SKIP_SCRIPTS=1; shift ;;
    --gate-port) GATE_PORT="$2"; shift 2 ;;
    --user) AS_USER="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

log() { echo "[wsl-install] $*"; }
die() { echo "[wsl-install] ERROR: $*" >&2; exit 1; }

# Root re-invocation: fix payload readability, then re-run as the target user
# for everything under $HOME (prefix steps are plain files, user-readable).
if [ -n "$AS_USER" ] && [ "$(id -u)" = "0" ]; then
  TARGET_HOME="$(getent passwd "$AS_USER" | cut -d: -f6)"
  [ -n "$TARGET_HOME" ] || die "no such user: $AS_USER"
  chmod -R a+rX "$SRC"
  CHILD_ARGS="--src '$SRC' --prefix '$PREFIX'"
  [ "$SKIP_TREE" = "1" ] && CHILD_ARGS="$CHILD_ARGS --skip-tree"
  [ "$SKIP_PROFILE" = "1" ] && CHILD_ARGS="$CHILD_ARGS --skip-profile"
  [ "$SKIP_SCRIPTS" = "1" ] && CHILD_ARGS="$CHILD_ARGS --skip-scripts"
  [ -n "$GATE_PORT" ] && CHILD_ARGS="$CHILD_ARGS --gate-port '$GATE_PORT'"
  # shellcheck disable=SC2086
  exec su -s /bin/bash "$AS_USER" -c "HOME='$TARGET_HOME' '$0' $CHILD_ARGS"
fi

[ -n "$SRC" ] || die "--src is required"
[ -d "$SRC/node" ] || die "payload incomplete: $SRC/node missing"
[ -d "$SRC/dsh" ] || die "payload incomplete: $SRC/dsh missing"
[ -n "$PREFIX" ] || PREFIX="$HOME/.dsh-bundle"

NODE_BIN="$SRC/node/bin/node"
[ -x "$NODE_BIN" ] || die "node binary missing in payload"
DSH_PKG_JSON="$SRC/dsh/@deepseek-ai/dsh/package.json"
[ -f "$DSH_PKG_JSON" ] || die "dsh package.json missing in payload"
DSH_REL_ENTRY="$("$SRC/node/bin/node" -e "const b=require('$DSH_PKG_JSON').bin; console.log(typeof b==='string'?b:b.dsh)")"
[ -n "$DSH_REL_ENTRY" ] || die "could not resolve dsh bin entry"

# ---------- 1. Portable tree ----------
if [ "$SKIP_TREE" != "1" ]; then
  mkdir -p "$PREFIX"
  # Single-stream tar pipe instead of `cp -a`: the payload usually sits on a 9P
  # mount (/mnt/c from the Windows installer). `cp -a` of the ~100k-file dsh
  # tree pays a per-file 9P round-trip (minutes); tarballing reads it as ONE
  # sequential stream, then extraction writes straight to ext4. Modes and
  # symlinks are preserved by the tar format (no dereferencing).
  log "copying portable tree -> $PREFIX (single-stream tar)"
  rm -rf "$PREFIX/node" "$PREFIX/dsh"
  tar -C "$SRC" -cf - node | tar -C "$PREFIX" -xf -
  tar -C "$SRC" -cf - dsh   | tar -C "$PREFIX" -xf -
else
  log "using payload in place at $PREFIX (--skip-tree)"
fi
NODE_BIN="$PREFIX/node/bin/node"
DSH_ENTRY="$PREFIX/dsh/@deepseek-ai/dsh/$DSH_REL_ENTRY"
[ -x "$NODE_BIN" ] || die "node missing at $NODE_BIN"

# ---------- 2. dsh shim ----------
BIN_DIR="$PREFIX/bin"
mkdir -p "$BIN_DIR"
cat > "$BIN_DIR/dsh" <<EOF
#!/bin/sh
exec "$NODE_BIN" "$DSH_ENTRY" "\$@"
EOF
chmod +x "$BIN_DIR/dsh"

LOCAL_BIN="$HOME/.local/bin"
mkdir -p "$LOCAL_BIN"
if [ -e "$LOCAL_BIN/dsh" ] && [ ! -L "$LOCAL_BIN/dsh" ]; then
  cp -a "$LOCAL_BIN/dsh" "$LOCAL_BIN/dsh.pre-bundle.bak"
  log "existing ~/.local/bin/dsh backed up (.pre-bundle.bak)"
fi
ln -sf "$BIN_DIR/dsh" "$LOCAL_BIN/dsh"
log "dsh shim: $BIN_DIR/dsh -> $DSH_ENTRY"
case ":$PATH:" in
  *":$LOCAL_BIN:"*) ;;
  *) log "NOTE: ~/.local/bin is not on PATH in this shell; new login shells usually have it" ;;
esac

# ---------- 3. Profile (fill gaps; never clobber credentials) ----------
if [ "$SKIP_PROFILE" != "1" ] && [ -d "$SRC/profile-web" ]; then
  if [ ! -d "$HOME/.dsh" ]; then
    cp -a "$SRC/profile-web" "$HOME/.dsh"
    log "profile installed -> $HOME/.dsh"
  else
    ( cd "$SRC/profile-web" && find . -type f | while read -r f; do
        target="$HOME/.dsh/$f"
        if [ ! -e "$target" ]; then
          mkdir -p "$(dirname "$target")"
          cp "$SRC/profile-web/$f" "$target"
        fi
      done )
    log "existing $HOME/.dsh kept; missing files filled from the bundle"
  fi

  # ---------- 3b. Manager plugin package + local pnpm store (portable profile) ----------
  # The bake records pnpm metadata against the CI store path and an absolute
  # file: path for the manager plugin. Rewire both for THIS machine so the first
  # `dsh plugin add/update` does not fail with ERR_PNPM_UNEXPECTED_STORE or a
  # file:D:\a\... not-found.
  if [ -d "$SRC/manager-pkg" ]; then
    if [ ! -d "$HOME/.dsh/manager-pkg" ]; then
      mkdir -p "$HOME/.dsh"
      cp -aL "$SRC/manager-pkg" "$HOME/.dsh/manager-pkg"
      log "manager plugin package -> $HOME/.dsh/manager-pkg"
    else
      ( cd "$SRC/manager-pkg" && find . -type f | while read -r f; do
          target="$HOME/.dsh/manager-pkg/$f"
          if [ ! -e "$target" ]; then
            mkdir -p "$(dirname "$target")"
            cp "$SRC/manager-pkg/$f" "$target"
          fi
        done )
      log "existing $HOME/.dsh/manager-pkg kept; missing files filled"
    fi
  fi

  MODULES_YAML="$HOME/.dsh/profiles/web/node_modules/.modules.yaml"
  if [ -f "$MODULES_YAML" ]; then
    # `pnpm config get store-dir` returns "undefined" unless explicitly set;
    # `pnpm store path` prints the actual computed absolute store location.
    STORE_DIR="$("$NODE_BIN" "$SRC/node/bin/pnpm" store path 2>/dev/null | tail -n 1 | tr -d '[:space:]')"
    if [ -n "$STORE_DIR" ]; then
      node -e "
const fs = require('fs');
const path = require('path');
const p = path.resolve('$MODULES_YAML');
let d = JSON.parse(fs.readFileSync(p, 'utf-8'));
d.storeDir = '$STORE_DIR';
d.virtualStoreDir = path.resolve('$HOME/.dsh/profiles/web/node_modules/.pnpm');
fs.writeFileSync(p, JSON.stringify(d, null, 2) + '\n');
" 2>/dev/null && log "pnpm store rewired -> $STORE_DIR" \
      || log "WARNING: could not rewire pnpm store (non-fatal)"
    fi
  fi
fi

# ---------- 4. Companion scripts ----------
if [ "$SKIP_SCRIPTS" != "1" ] && [ -d "$SRC/wsl-scripts" ]; then
  mkdir -p "$HOME/.dsh-webui"
  for f in "$SRC"/wsl-scripts/*.sh; do
    [ -f "$f" ] || continue
    base="$(basename "$f")"
    cp "$f" "$HOME/.dsh-webui/.new-$base"
    chmod +x "$HOME/.dsh-webui/.new-$base"
    mv -f "$HOME/.dsh-webui/.new-$base" "$HOME/.dsh-webui/$base"   # atomic-ish
  done
  log "companion scripts refreshed -> $HOME/.dsh-webui"
fi

# ---------- 5. Offline-start gate ----------
if [ -n "$GATE_PORT" ]; then
  log "offline-start gate (dead proxies, port $GATE_PORT)"
  ( cd "$HOME" && \
    env HTTP_PROXY=http://127.0.0.1:9 HTTPS_PROXY=http://127.0.0.1:9 NO_PROXY= \
    "$NODE_BIN" "$DSH_ENTRY" --profile web --host 127.0.0.1 --port "$GATE_PORT" --no-open \
    > "$HOME/.dsh-webui/gate.out.log" 2> "$HOME/.dsh-webui/gate.err.log" ) &
  GATE_PID=$!
  OK=0
  for _ in $(seq 1 30); do
    sleep 1
    if (echo > /dev/tcp/127.0.0.1/"$GATE_PORT") >/dev/null 2>&1; then OK=1; break; fi
  done
  pkill -P "$GATE_PID" 2>/dev/null || true; kill "$GATE_PID" 2>/dev/null || true
  if [ "$OK" != "1" ]; then
    tail -n 10 "$HOME/.dsh-webui/gate.out.log" "$HOME/.dsh-webui/gate.err.log" 2>/dev/null | sed 's/^/  /'
    die "offline-start gate FAILED (logs above)"
  fi
  log "offline-start gate passed"
fi

log "done. dsh --version: $("$LOCAL_BIN/dsh" --version 2>/dev/null || echo '(run in a login shell)')"
