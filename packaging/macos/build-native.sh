#!/usr/bin/env bash
# Builds the BrainFuel macOS app bundle and DMG (osx-arm64).
# Adapted from MoveBit's packaging script — same Avalonia single-file payload,
# same icns-from-PNG pipeline. Version is injected, never hardcoded.
#
# Usage: build-native.sh <version> [output-dir]
set -euo pipefail

version="${1:?usage: build-native.sh <version> [output-dir]}"
out_dir="${2:-dist/native-macos}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$root/build/native-macos"
publish="$work/publish"
app="$work/BrainFuel.app"

rm -rf "$work" "$root/$out_dir"
mkdir -p "$publish" "$app/Contents/MacOS" "$app/Contents/Resources" "$root/$out_dir"

cd "$root"
dotnet publish BrainFuel.csproj -c Release -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false \
  -o "$publish"

find "$publish" -name '*.pdb' -delete
cp -R "$publish"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/BrainFuel"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
  <key>CFBundleDisplayName</key><string>BrainFuel</string>
  <key>CFBundleExecutable</key><string>BrainFuel</string>
  <key>CFBundleIdentifier</key><string>com.turinglambdaai.brainfuel</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>BrainFuel</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version}</string>
  <key>CFBundleVersion</key><string>${version}</string>
  <key>CFBundleIconFile</key><string>BrainFuel</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

iconset="$work/BrainFuel.iconset"
mkdir -p "$iconset"
source_icon="$root/Assets/icon.png"
for spec in \
  '16 icon_16x16.png' \
  '32 icon_16x16@2x.png' \
  '32 icon_32x32.png' \
  '64 icon_32x32@2x.png' \
  '128 icon_128x128.png' \
  '256 icon_128x128@2x.png' \
  '256 icon_256x256.png' \
  '512 icon_256x256@2x.png' \
  '512 icon_512x512.png' \
  '1024 icon_512x512@2x.png'; do
  size="${spec%% *}"
  name="${spec#* }"
  sips -z "$size" "$size" "$source_icon" --out "$iconset/$name" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/BrainFuel.icns"

plutil -lint "$app/Contents/Info.plist"
test -x "$app/Contents/MacOS/BrainFuel"

app_zip="$root/$out_dir/BrainFuel-macos-arm64.app.zip"
dmg="$root/$out_dir/BrainFuel-macos-arm64.dmg"
ditto -c -k --sequesterRsrc --keepParent "$app" "$app_zip"

dmg_root="$work/dmg-root"
mkdir -p "$dmg_root"
ditto "$app" "$dmg_root/BrainFuel.app"
ln -s /Applications "$dmg_root/Applications"
hdiutil create -quiet -volname BrainFuel -srcfolder "$dmg_root" -ov -format UDZO "$dmg"
hdiutil verify "$dmg" >/dev/null

echo "built:"
ls -la "$root/$out_dir"
