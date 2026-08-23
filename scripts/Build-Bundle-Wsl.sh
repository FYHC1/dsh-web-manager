#!/usr/bin/env bash
# ============================================================
#  dsh-offline-bundle WSL payload builder. Runs on LINUX
#  (GitHub Actions ubuntu runner, or inside a WSL distro).
#
#  Produces a self-contained WSL-side payload:
#
#    <OutDir>/bundle-wsl/
#      node/            portable Node linux-x64 (tar.xz from nodejs.org / npmmirror)
#      dsh/             @deepseek-ai/dsh npm tree (hoisted, linux natives)
#      profile-web/     pre-baked ~/.dsh for Linux (first-run + manager plugin)
#      wsl-scripts/     manager companion scripts (wsl-start.sh etc.)
#      install-wsl.sh   in-distro installer (also used by the deb's postinst)
#      wsl-bundle.json  manifest
#
#  The bake is gated the same way as the Windows side: the profile is
#  only packaged after a start with dead proxies succeeds.
#
#  Usage:
#    scripts/Build-Bundle-Wsl.sh                          # defaults
#    scripts/Build-Bundle-Wsl.sh --dsh-version 0.1.1-rc.2 --port 3206
# ============================================================
set -euo pipefail

NODE_VERSION="24.19.0"
DSH_VERSION="latest"
NODE_SOURCE="npmmirror"
OUTDIR=""
BAKE_PORT="3206"
PLUGIN_SRC=""          # repo root for `dsh plugin add file:<path>`; default: parent of this script

while [ $# -gt 0 ]; do
  case "$1" in
    --node-version) NODE_VERSION="$2"; shift 2 ;;
    --dsh-version) DSH_VERSION="$2"; shift 2 ;;
    --node-source) NODE_SOURCE="$2"; shift 2 ;;
    --out) OUTDIR="$2"; shift 2 ;;
    --port) BAKE_PORT="$2"; shift 2 ;;
    --plugin-src) PLUGIN_SRC="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
[ -n "$PLUGIN_SRC" ] || PLUGIN_SRC="$REPO_ROOT"
[ -n "$OUTDIR" ] || OUTDIR="$REPO_ROOT/bundle-out"
BUNDLE="$OUTDIR/bundle-wsl"

log() { echo "[wsl-bundle] $*"; }
die() { echo "[wsl-bundle] ERROR: $*" >&2; exit 1; }

command -v curl >/dev/null || die "curl is required"
command -v tar  >/dev/null || die "tar is required"
command -v xz   >/dev/null || die "xz is required"

# ---------- 1. Portable Node (linux-x64) ----------
mkdir -p "$OUTDIR" "$BUNDLE"
NODE_DIR="$BUNDLE/node"
rm -rf "$NODE_DIR"
TARBALL="node-v$NODE_VERSION-linux-x64.tar.xz"
case "$NODE_SOURCE" in
  npmmirror) URL="https://cdn.npmmirror.com/binaries/node/v$NODE_VERSION/$TARBALL" ;;
  nodejs)    URL="https://nodejs.org/dist/v$NODE_VERSION/$TARBALL" ;;
  *) URL="$NODE_SOURCE" ;;
esac
TARBALL_PATH="$OUTDIR/$TARBALL"
if [ ! -f "$TARBALL_PATH" ]; then
  log "GET $URL"
  curl -fL --retry 3 -o "$TARBALL_PATH" "$URL"
fi
rm -rf "$OUTDIR/node-linux-extract"
mkdir -p "$OUTDIR/node-linux-extract"
tar -xJf "$TARBALL_PATH" -C "$OUTDIR/node-linux-extract"
INNER="$(find "$OUTDIR/node-linux-extract" -mindepth 1 -maxdepth 1 -type d | head -1)"
[ -n "$INNER" ] || die "node tarball extraction produced no folder"
mv "$INNER" "$NODE_DIR"
rm -rf "$OUTDIR/node-linux-extract"
NODE_BIN="$NODE_DIR/bin/node"
[ -x "$NODE_BIN" ] || die "node binary missing after extract"
log "node $("$NODE_BIN" --version) -> $NODE_DIR"

# ---------- 2. dsh npm tree (hoisted, linux natives) ----------
DSH_DIR="$BUNDLE/dsh"
rm -rf "$DSH_DIR"
STAGE="$OUTDIR/dsh-linux-stage"
rm -rf "$STAGE"; mkdir -p "$STAGE"
# A package.json HERE is required: npm walks up to the nearest one and would
# otherwise install into the repo root (which owns the manager plugin package).
echo '{"name":"dsh-bundle-stage","private":true}' > "$STAGE/package.json"
log "npm install @deepseek-ai/dsh@$DSH_VERSION (npmmirror, global-style)"
(
  cd "$STAGE"
  "$NODE_BIN" "$NODE_DIR/lib/node_modules/npm/bin/npm-cli.js" install \
    "@deepseek-ai/dsh@$DSH_VERSION" --global-style --omit=dev \
    --no-audit --no-fund --loglevel=error --registry=https://registry.npmmirror.com
)
[ -d "$STAGE/node_modules" ] || die "npm install produced no node_modules"
mv "$STAGE/node_modules" "$DSH_DIR"
rm -rf "$STAGE"
DSH_PKG_JSON="$DSH_DIR/@deepseek-ai/dsh/package.json"
[ -f "$DSH_PKG_JSON" ] || die "dsh package.json missing in tree"
log "dsh $(node -e "console.log(require('$DSH_PKG_JSON').version)") packaged"

# dsh's `plugin` subcommand shells out to pnpm found on PATH. Ship pnpm inside
# the portable node so the bake — and the offline target machine — never need a
# global pnpm (a clean CI runner has none; exit 127 otherwise).
"$NODE_BIN" "$NODE_DIR/lib/node_modules/npm/bin/npm-cli.js" install -g pnpm \
  --no-audit --no-fund --loglevel=error --registry=https://registry.npmmirror.com
log "pnpm $("$NODE_DIR/bin/pnpm" --version) bundled into the portable node"

# ---------- 3. Companion scripts ----------
mkdir -p "$BUNDLE/wsl-scripts"
cp -f "$REPO_ROOT"/scripts/wsl/*.sh "$BUNDLE/wsl-scripts/" 2>/dev/null || log "WARNING: no scripts/wsl/*.sh found"
log "companion scripts copied"

# ---------- 4. Profile bake (isolated HOME, offline-start gated) ----------
BAKE_HOME="$OUTDIR/bake-home-linux"
rm -rf "$BAKE_HOME"
mkdir -p "$BAKE_HOME/.dsh" "$BAKE_HOME/.cache" "$BAKE_HOME/.local/share"

DSH_REL_ENTRY="$(node -e "const b=require('$DSH_PKG_JSON').bin; console.log(typeof b==='string'?b:b.dsh)")"
[ -n "$DSH_REL_ENTRY" ] || die "could not resolve dsh bin entry"
DSH_ENTRY="$DSH_DIR/@deepseek-ai/dsh/$DSH_REL_ENTRY"

run_dsh() {  # $1 = 0|1 offline, rest = dsh args; stdout/stderr -> bake logs
  local offline="$1"; shift
  local tag="$1"; shift
  local env_prefix=()
  if [ "$offline" = "1" ]; then
    env_prefix=(env HTTP_PROXY=http://127.0.0.1:9 HTTPS_PROXY=http://127.0.0.1:9 NO_PROXY=)
  else
    env_prefix=(env)
  fi
  ( cd "$BAKE_HOME" && "${env_prefix[@]}" \
      PATH="$NODE_DIR/bin:$PATH" \
      HOME="$BAKE_HOME" \
      XDG_DATA_HOME="$BAKE_HOME/.local/share" \
      XDG_CACHE_HOME="$BAKE_HOME/.cache" \
      DSH_HOME="$BAKE_HOME/.dsh" \
      "$NODE_BIN" "$DSH_ENTRY" "$@" \
      > "$OUTDIR/bake-linux-$tag.out.log" 2> "$OUTDIR/bake-linux-$tag.err.log" ) &
  BAKE_PID=$!
}

wait_port() {  # $1 = seconds
  local deadline=$(( $(date +%s) + $1 ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    sleep 1
    if (echo > /dev/tcp/127.0.0.1/$BAKE_PORT) >/dev/null 2>&1; then return 0; fi
  done
  return 1
}

show_logs() {
  local tag="$1"
  for f in "$OUTDIR/bake-linux-$tag.out.log" "$OUTDIR/bake-linux-$tag.err.log"; do
    if [ -s "$f" ]; then echo "---- $f (tail) ----"; tail -n 15 "$f" | sed 's/^/  /'; fi
  done
}

# 4a. First-run init.
log "bake: first-run init (dsh --profile web --port $BAKE_PORT)"
run_dsh 0 firstrun --profile web --host 127.0.0.1 --port "$BAKE_PORT" --no-open
if ! wait_port 40; then
  show_logs firstrun
  kill "$BAKE_PID" 2>/dev/null || true
  die "first-run bake failed: dsh did not serve the port within 40s (logs above)"
fi
pkill -P "$BAKE_PID" 2>/dev/null || true; kill "$BAKE_PID" 2>/dev/null || true
sleep 1

# 4b. Manager plugin into the baked profile.
#     (`plugin` is a subcommand with its OWN required --profile.)
log "bake: dsh plugin --profile web add file:$PLUGIN_SRC"
run_dsh 0 pluginadd plugin --profile web add "file:$PLUGIN_SRC"
EXIT_CODE=0
wait "$BAKE_PID" || EXIT_CODE=$?
if [ "$EXIT_CODE" -ne 0 ]; then
  show_logs pluginadd
  die "plugin add failed with exit code $EXIT_CODE (logs above)"
fi

# 4c. Offline-start gate (dead proxies).
log "bake: offline-start verification (dead proxies)"
run_dsh 1 offline --profile web --host 127.0.0.1 --port "$BAKE_PORT" --no-open
OFFLINE_OK=0
if wait_port 40; then OFFLINE_OK=1; fi
pkill -P "$BAKE_PID" 2>/dev/null || true; kill "$BAKE_PID" 2>/dev/null || true
if [ "$OFFLINE_OK" != "1" ]; then
  show_logs offline
  die "offline-start gate FAILED (logs above): profile needs the network; do not ship it"
fi

PROFILE_DIR="$BUNDLE/profile-web"
rm -rf "$PROFILE_DIR"
mv "$BAKE_HOME/.dsh" "$PROFILE_DIR"
rm -rf "$BAKE_HOME"
log "profile baked -> $PROFILE_DIR (offline start verified)"

# ---------- 5. Installer + manifest ----------
cp -f "$SCRIPT_DIR/wsl/install-wsl.sh" "$BUNDLE/install-wsl.sh"
chmod +x "$BUNDLE/install-wsl.sh"

cat > "$BUNDLE/wsl-bundle.json" <<EOF
{
  "NodeVersion": "$("$NODE_BIN" --version)",
  "DshVersion": "$(node -e "console.log(require('$DSH_PKG_JSON').version)")",
  "DshEntry": "$DSH_REL_ENTRY",
  "ProfileOfflineStartVerified": true,
  "BakePort": $BAKE_PORT
}
EOF

log "OK: $BUNDLE ($(du -sh "$BUNDLE" | cut -f1))"
