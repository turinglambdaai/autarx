#!/bin/sh
# Build the distributable autarx CLI: launcher + embedded Racket runtime
# tree + rivet-app-info.rktd, zipped for the update feed. The zip layout is
# exactly what `autarx update` swaps over an install directory (marker file:
# the `autarx` launcher).
#
# Usage: scripts/build-cli.sh <output-dir>
# Produces <output-dir>/autarx/ (the distributable) and
#         <output-dir>/autarx-cli-<version>-<platform>.zip
set -eu

OUT_DIR="${1:?usage: build-cli.sh <output-dir>}"
cd "$(dirname "$0")/.."

VERSION=$(racket -e '(begin (require racket/file) (display (hash-ref (file->value "rivet.rktd") (quote version))))')
UNAME_S=$(uname -s)
if [ "$UNAME_S" = "Darwin" ]; then OS=macos; elif [ "$UNAME_S" = "Linux" ]; then OS=linux; else OS=windows; fi
UNAME_M=$(uname -m)
if [ "$UNAME_M" = "arm64" ] || [ "$UNAME_M" = "aarch64" ]; then ARCH=arm64; else ARCH=x64; fi

DIST="$OUT_DIR/autarx"
rm -rf "$DIST"
mkdir -p "$OUT_DIR"

echo "build-cli: launcher…"
raco exe -o "$OUT_DIR/launcher-staging" racket/autarx/cli.rkt

echo "build-cli: collect runtime (raco distribute)…"
# raco exe writes the launcher read-only; distribute patches it in place
chmod u+w "$OUT_DIR/launcher-staging"
# distribute's rpath expects bin/ + lib/ siblings — keep that layout, the
# update payload swaps over the same shape
raco distribute "$OUT_DIR/collect" "$OUT_DIR/launcher-staging"
rm -rf "$DIST"
mv "$OUT_DIR/collect" "$DIST"
mv "$DIST/bin/launcher-staging" "$DIST/bin/autarx"
chmod 555 "$DIST/bin/autarx"
rm -f "$OUT_DIR/launcher-staging"

echo "build-cli: write rivet-app-info.rktd…"
racket -e "(begin (require racket/file) (write-to-file (file->value \"rivet.rktd\") \"rivet-app-info.rktd\" #:exists 'truncate/replace))"
mv rivet-app-info.rktd "$DIST/rivet-app-info.rktd"

echo "build-cli: smoke (version must read from the metadata file)…"
"$DIST/bin/autarx" --help | head -1

ZIP="$OUT_DIR/autarx-cli-$VERSION-$OS-$ARCH.zip"
if command -v zip >/dev/null 2>&1; then
  (cd "$DIST" && zip -qry "$ZIP" .)
else
  # windows runners ship PowerShell instead of zip
  powershell.exe -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$DIST")/*' -DestinationPath '$(cygpath -w "$ZIP")' -Force"
fi
echo "build-cli: $ZIP"
