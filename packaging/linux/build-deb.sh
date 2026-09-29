#!/usr/bin/env bash
set -euo pipefail

# Package Autarx as two Debian packages (family convention: product-os-arch,
# no version in the asset name — the release tag carries the version).
#   usage: build-deb.sh <version> [output-dir]
#   - autarx-cli: /opt/autarx-cli + /usr/bin/autarx symlink
#   - autarx-gui: /opt/autarx-gui + /usr/bin/autarx-gui symlink + desktop entry

version="${1:?usage: build-deb.sh <version> [output-dir]}"
case "$version" in
  *[!0-9.]*) version="0.0.0" ;;   # manual workflow_dispatch runs without a tag
esac
out_dir="${2:-publish}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$root/build/deb-linux"

rm -rf "$work"
mkdir -p "$work/cli" "$work/gui" \
  "$work/pkg-cli/DEBIAN" "$work/pkg-cli/opt/autarx-cli" "$work/pkg-cli/usr/bin" \
  "$work/pkg-gui/DEBIAN" "$work/pkg-gui/opt/autarx-gui" "$work/pkg-gui/usr/bin" \
  "$work/pkg-gui/usr/share/applications" "$root/$out_dir"

cd "$root"
flags=(--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false)
dotnet publish src/Autarx.Cli/Autarx.Cli.csproj -c Release -r linux-x64 "${flags[@]}" -o "$work/cli"
dotnet publish src/Autarx.Gui/Autarx.Gui.csproj -c Release -r linux-x64 "${flags[@]}" -o "$work/gui"

# --- autarx-cli ---
install -m 0755 "$work/cli/autarx" "$work/pkg-cli/opt/autarx-cli/autarx"
ln -s /opt/autarx-cli/autarx "$work/pkg-cli/usr/bin/autarx"

cat > "$work/pkg-cli/DEBIAN/control" <<EOF
Package: autarx-cli
Version: ${version}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: turinglambdaai
Homepage: https://autarx.jrtx.site/
Depends: libicu72 | libicu74 | libicu76
Description: AUTOSAR integration workbench — CLI
 Semantic index for OEM AUTOSAR deliveries: reference tracing, semantic
 diff, ECU impact analysis and reviewed patching, with an MCP interface
 for AI agents.
EOF

deb_cli="$root/$out_dir/Autarx-cli-linux-x64.deb"
dpkg-deb --build --root-owner-group "$work/pkg-cli" "$deb_cli"
dpkg-deb --info "$deb_cli" >/dev/null
dpkg-deb --contents "$deb_cli" | grep -q './opt/autarx-cli/autarx$'

# --- autarx-gui ---
find "$work/gui" -name '*.pdb' -delete
cp -R "$work/gui"/. "$work/pkg-gui/opt/autarx-gui/"
chmod +x "$work/pkg-gui/opt/autarx-gui/Autarx"
ln -s /opt/autarx-gui/Autarx "$work/pkg-gui/usr/bin/autarx-gui"

cat > "$work/pkg-gui/usr/share/applications/autarx-gui.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Autarx
Comment=AUTOSAR integration workbench
Exec=/opt/autarx-gui/Autarx
Terminal=false
Categories=Development;
StartupNotify=false
EOF

cat > "$work/pkg-gui/DEBIAN/control" <<EOF
Package: autarx-gui
Version: ${version}
Section: devel
Priority: optional
Architecture: amd64
Maintainer: turinglambdaai
Homepage: https://autarx.jrtx.site/
Depends: libx11-6, libxss1, libxkbcommon0, libfontconfig1, libicu72 | libicu74 | libicu76
Suggests: dbus, libglib2.0-bin
Description: AUTOSAR integration workbench — GUI
 Semantic index for OEM AUTOSAR deliveries: reference tracing, semantic
 diff, ECU impact analysis, communication projection and reviewed patching.
EOF

deb_gui="$root/$out_dir/Autarx-gui-linux-x64.deb"
dpkg-deb --build --root-owner-group "$work/pkg-gui" "$deb_gui"
dpkg-deb --info "$deb_gui" >/dev/null
dpkg-deb --contents "$deb_gui" | grep -q './opt/autarx-gui/Autarx$'

for deb in "$deb_cli" "$deb_gui"; do
  hash="$(sha256sum "$deb" | awk '{print $1}')"
  printf '%s  %s' "$hash" "$(basename "$deb")" > "$deb.sha256"
  echo "packaged: $deb"
done
