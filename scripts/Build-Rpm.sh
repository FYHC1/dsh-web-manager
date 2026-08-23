#!/usr/bin/env bash
# ============================================================
#  dsh-bundle-wsl RPM builder (Fedora-family WSL distros).
#  Runs where rpmbuild exists (Fedora WSL, CI fedora container).
#
#  Produces: dsh-bundle-wsl-<version>-1.x86_64.rpm
#    /opt/dsh-bundle-wsl/{node,dsh,profile-web,wsl-scripts,install-wsl.sh}
#    /usr/local/bin/dsh-bundle-wsl-install
#  %post wires the default WSL user (same behaviour as the deb).
#
#  The `-wsl` name suffix distinguishes this package from future
#  native-Linux (bare-metal) dsh-bundle packages.
#
#  Usage: scripts/Build-Rpm.sh [--payload DIR] [--version X.Y.Z] [--out DIR]
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

command -v rpmbuild >/dev/null || { echo "rpmbuild is required (dnf install rpm-build / CI fedora container)"; exit 1; }

for part in node dsh profile-web wsl-scripts install-wsl.sh; do
  [ -e "$PAYLOAD/$part" ] || { echo "payload incomplete: $PAYLOAD/$part missing (run Build-Bundle-Wsl.sh first)"; exit 1; }
done

WORK="$OUT/rpm-work"
rm -rf "$WORK"
mkdir -p "$WORK/stage" "$WORK/SOURCES" "$WORK/SPECS" "$WORK/BUILD" "$WORK/BUILDROOT" "$WORK/RPMS" "$WORK/SRPMS"

# ---------- stage the file tree ----------
OPT="$WORK/stage/opt/dsh-bundle-wsl"
mkdir -p "$OPT" "$WORK/stage/usr/local/bin"
cp -a "$PAYLOAD/node" "$PAYLOAD/dsh" "$PAYLOAD/profile-web" "$PAYLOAD/wsl-scripts" \
      "$PAYLOAD/install-wsl.sh" "$OPT/"
chmod +x "$OPT/install-wsl.sh"

cat > "$WORK/stage/usr/local/bin/dsh-bundle-wsl-install" <<'EOF'
#!/bin/bash
# Re-run the dsh-bundle-wsl user-level install (profile/scripts/shim) for $USER.
exec /opt/dsh-bundle-wsl/install-wsl.sh --src /opt/dsh-bundle-wsl --prefix /opt/dsh-bundle-wsl --skip-tree "$@"
EOF
chmod +x "$WORK/stage/usr/local/bin/dsh-bundle-wsl-install"

tar -C "$WORK/stage" -cf "$WORK/SOURCES/dsh-bundle-wsl-tree.tar" .

# ---------- spec ----------
POST_SCRIPT="$(cat "$REPO_ROOT/packaging/rpm/post")"
cat > "$WORK/SPECS/dsh-bundle-wsl.spec" <<EOF
Name: dsh-bundle-wsl
Version: $VERSION
Release: 1
Summary: Offline dsh bundle for WSL - portable Node + dsh + pre-baked profile
License: MIT
BuildArch: x86_64
AutoReqProv: no
Source0: dsh-bundle-wsl-tree.tar

%description
Self-contained dsh (DeepSeek Harness) runtime for OFFLINE WSL distros
(Fedora-family): portable Node.js, the dsh npm tree and a pre-baked
~/.dsh profile with the dsh-web-manager plugin (offline-start verified
at build time). Installed to /opt/dsh-bundle-wsl; %post wires the
default WSL user (shim in ~/.local/bin, profile fill-gaps, companion
scripts). The -wsl suffix distinguishes this from future native-Linux
dsh-bundle packages.

%prep

%build

%install
rm -rf "%{buildroot}"
mkdir -p "%{buildroot}"
tar -xf "%{_sourcedir}/dsh-bundle-wsl-tree.tar" -C "%{buildroot}"

%files
/opt/dsh-bundle-wsl
/usr/local/bin/dsh-bundle-wsl-install

%post
$POST_SCRIPT
exit 0
EOF

rpmbuild -bb \
  --define "_topdir $WORK" \
  --define "_sourcedir $WORK/SOURCES" \
  --define "_specdir $WORK/SPECS" \
  --define "_builddir $WORK/BUILD" \
  --define "_buildrootdir $WORK/BUILDROOT" \
  --define "_rpmdir $WORK/RPMS" \
  --define "_srcrpmdir $WORK/SRPMS" \
  "$WORK/SPECS/dsh-bundle-wsl.spec" || { echo "rpmbuild failed"; exit 1; }

mkdir -p "$OUT"
find "$WORK/RPMS" -name '*.rpm' -exec cp -f {} "$OUT/" \;
rm -rf "$WORK"
echo "rpm built: $(ls "$OUT"/dsh-bundle-wsl-*.rpm | head -1) ($(du -h "$OUT"/dsh-bundle-wsl-*.rpm | head -1 | cut -f1))"
