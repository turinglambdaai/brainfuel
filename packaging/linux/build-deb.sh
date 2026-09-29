#!/usr/bin/env bash
set -euo pipefail

# Package BrainFuel as a Debian package (family convention: product-os-arch,
# no version in the asset name — the release tag carries the version).
#   usage: build-deb.sh <version> [output-dir]
# Layout: /opt/brainfuel/BrainFuel + /usr/bin/brainfuel symlink + desktop entry.

version="${1:?usage: build-deb.sh <version> [output-dir]}"
case "$version" in
  *[!0-9.]*) version="0.0.0" ;;   # manual workflow_dispatch runs without a tag
esac
out_dir="${2:-dist/deb}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$root/build/deb-linux"
publish="$work/publish"
pkg="$work/pkg"

rm -rf "$work"
mkdir -p "$publish" "$pkg/DEBIAN" "$pkg/opt/brainfuel" "$pkg/usr/bin" \
  "$pkg/usr/share/applications" "$pkg/usr/share/icons/hicolor/256x256/apps" "$root/$out_dir"

cd "$root"
dotnet publish BrainFuel.csproj -c Release -r linux-x64 \
  --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false \
  -o "$publish"

find "$publish" -name '*.pdb' -delete
cp -R "$publish"/. "$pkg/opt/brainfuel/"
chmod +x "$pkg/opt/brainfuel/BrainFuel"

cat > "$pkg/DEBIAN/control" <<EOF
Package: brainfuel
Version: ${version}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: turinglambdaai
Homepage: https://brainfuel.jrtx.site/
Depends: libx11-6, libxss1, libxkbcommon0, libfontconfig1, libicu72 | libicu74 | libicu76
Suggests: dbus, libglib2.0-bin
Description: GLM Coding Plan quota monitor
 BrainFuel is an always-on-top desktop widget tracking your 5-hour window
 and weekly allowance, so rate limits never surprise you.
EOF

cat > "$pkg/usr/share/applications/brainfuel.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=BrainFuel
Comment=GLM Coding Plan quota monitor
Exec=/opt/brainfuel/BrainFuel
Icon=brainfuel
Terminal=false
Categories=Utility;Development;
StartupNotify=false
EOF

cp "$root/Assets/icon.png" "$pkg/usr/share/icons/hicolor/256x256/apps/brainfuel.png"
ln -s /opt/brainfuel/BrainFuel "$pkg/usr/bin/brainfuel"

deb="$root/$out_dir/BrainFuel-linux-x64.deb"
dpkg-deb --build --root-owner-group "$pkg" "$deb"
dpkg-deb --info "$deb" >/dev/null
dpkg-deb --contents "$deb" | grep -q './opt/brainfuel/BrainFuel$'

hash="$(sha256sum "$deb" | awk '{print $1}')"
printf '%s  %s' "$hash" "$(basename "$deb")" > "$deb.sha256"
echo "packaged: $deb"
