#!/usr/bin/env bash
# ============================================================
#  dsh-bundle-wsl deb builder (Debian-family WSL distros).
#  Runs on LINUX (needs dpkg-deb).
#
#  Produces: dsh-bundle-wsl_<version>_amd64.deb
#    /opt/dsh-bundle-wsl/{node,dsh,profile-web,wsl-scripts,install-wsl.sh}
#    /usr/local/bin/dsh-bundle-wsl-install
#  postinst wires the default WSL user via install-wsl.sh.
#
#  The `-wsl` name suffix distinguishes this package from future
#  native-Linux (bare-metal) dsh-bundle packages.
#
#  Usage: scripts/Build-Deb.sh [--payload DIR] [--version X.Y.Z] [--out DIR]
# ============================================================
set -euo pipefail

PAYLOAD=""
VERSION=""
OUT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --payload) PAYLOAD="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
[ -n "$PAYLOAD" ] || PAYLOAD="$REPO_ROOT/bundle-out/bundle-wsl"
[ -n "$OUT" ] || OUT="$REPO_ROOT/bundle-out"
[ -n "$VERSION" ] || VERSION="$(grep -oP '(?<=AssemblyVersion\(")[0-9.]+' "$REPO_ROOT/src/AssemblyInfo.cs" | cut -d. -f1-3)"

command -v dpkg-deb >/dev/null || { echo "dpkg-deb is required (run on Debian/Ubuntu or CI ubuntu)"; exit 1; }

for part in node dsh profile-web wsl-scripts install-wsl.sh; do
  [ -e "$PAYLOAD/$part" ] || { echo "payload incomplete: $PAYLOAD/$part missing (run Build-Bundle-Wsl.sh first)"; exit 1; }
done

PKG="$OUT/deb-root"
rm -rf "$PKG"
mkdir -p "$PKG/opt/dsh-bundle-wsl" "$PKG/usr/local/bin" "$PKG/DEBIAN"

cp -a "$PAYLOAD/node" "$PAYLOAD/dsh" "$PAYLOAD/profile-web" "$PAYLOAD/wsl-scripts" \
      "$PAYLOAD/install-wsl.sh" "$PKG/opt/dsh-bundle-wsl/"
chmod +x "$PKG/opt/dsh-bundle-wsl/install-wsl.sh"

cat > "$PKG/usr/local/bin/dsh-bundle-wsl-install" <<'EOF'
#!/bin/bash
# Re-run the dsh-bundle-wsl user-level install (profile/scripts/shim) for $USER.
exec /opt/dsh-bundle-wsl/install-wsl.sh --src /opt/dsh-bundle-wsl --prefix /opt/dsh-bundle-wsl --skip-tree "$@"
EOF
chmod +x "$PKG/usr/local/bin/dsh-bundle-wsl-install"

SIZE_KB="$(du -sk --apparent-size "$PKG" | cut -f1)"
cat > "$PKG/DEBIAN/control" <<EOF
Package: dsh-bundle-wsl
Version: $VERSION
Architecture: amd64
Maintainer: dsh-web-manager contributors
Section: devel
Priority: optional
Installed-Size: $SIZE_KB
Depends:
Recommends:
Provides: dsh-bundle-wsl (= $VERSION)
Description: Offline dsh bundle for WSL - portable Node + @deepseek-ai/dsh + pre-baked profile
 Self-contained dsh (DeepSeek Harness) runtime for offline WSL distros:
 portable Node.js, the dsh npm tree and a pre-baked ~/.dsh profile with the
 dsh-web-manager plugin (offline-start verified at build time). Installed to
 /opt/dsh-bundle-wsl; postinst wires the default WSL user (shim in
 ~/.local/bin, profile fill-gaps, manager companion scripts). The -wsl
 suffix distinguishes this from future native-Linux dsh-bundle packages.
EOF

cp "$SCRIPT_DIR/../packaging/deb/postinst" "$PKG/DEBIAN/postinst"
chmod 755 "$PKG/DEBIAN/postinst"
[ -f "$SCRIPT_DIR/../packaging/deb/prerm" ] && cp "$SCRIPT_DIR/../packaging/deb/prerm" "$PKG/DEBIAN/prerm" && chmod 755 "$PKG/DEBIAN/prerm"

mkdir -p "$OUT"
DEB="$OUT/dsh-bundle-wsl_${VERSION}_amd64.deb"
rm -f "$DEB"
dpkg-deb --build --root-owner-group "$PKG" "$DEB"
rm -rf "$PKG"
echo "deb built: $DEB ($(du -h "$DEB" | cut -f1))"
