#!/bin/bash
# Builds Joycon2Ble.bundle (universal arm64 + x86_64) into Assets/Joycon2/Plugins/macOS/ and the console
# probe into build/. Needs Xcode command line tools only.
#
# The Unity Editor cannot unload a native bundle: once Play Mode has used it, quit Unity before rebuilding.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/build"
plugin_dir="$here/../../Assets/Joycon2/Plugins/macOS"
bundle="$out/Joycon2Ble.bundle"
min_os=11.0
flags=(-arch arm64 -arch x86_64 -mmacosx-version-min=$min_os -fobjc-arc -O2 -Wall -Wextra -Werror
       -I "$here/../windows/include")

rm -rf "$bundle"
mkdir -p "$bundle/Contents/MacOS"

clang "${flags[@]}" -bundle -fvisibility=hidden -framework Foundation -framework CoreBluetooth \
      -o "$bundle/Contents/MacOS/Joycon2Ble" "$here/src/joycon2ble.m"

cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleExecutable</key><string>Joycon2Ble</string>
    <key>CFBundleIdentifier</key><string>com.joycontest.joycon2ble</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>Joycon2Ble</string>
    <key>CFBundlePackageType</key><string>BNDL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>$min_os</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: enough for local runs (arm64 refuses unsigned code).
codesign --force --sign - "$bundle"

# Probe: loads the bundle like Unity does (dlopen) and prints events. It has to be an .app: TCC kills a bare
# executable on the first CoreBluetooth call, since it only accepts NSBluetoothAlwaysUsageDescription from a
# real bundle's Info.plist. Run build/joycon2probe.app/Contents/MacOS/joycon2probe.
probe="$out/joycon2probe.app"
rm -rf "$probe" "$out/joycon2probe"
mkdir -p "$probe/Contents/MacOS"
cp "$here/tools/probe-Info.plist" "$probe/Contents/Info.plist"
clang "${flags[@]}" -framework Foundation -o "$probe/Contents/MacOS/joycon2probe" "$here/tools/joycon2probe.m"
codesign --force --sign - "$probe"

# WebGL helper: lifts Joy-Con 2 connected by Chrome to ~66 Hz (tools/joycon2boost.m).
boost="$out/joycon2boost.app"
rm -rf "$boost"
mkdir -p "$boost/Contents/MacOS"
cp "$here/tools/boost-Info.plist" "$boost/Contents/Info.plist"
clang "${flags[@]}" -framework Foundation -framework CoreBluetooth -framework AppKit \
      -o "$boost/Contents/MacOS/joycon2boost" "$here/tools/joycon2boost.m"
codesign --force --sign - "$boost"
# Register joycon2boost:// for this build, and zip it for the in-game download (KagefumiWebGLBuild copies it).
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$boost"
rm -f "$out/Joycon2Boost-mac.zip"
ditto -c -k --keepParent "$boost" "$out/Joycon2Boost-mac.zip"

mkdir -p "$plugin_dir"
rm -rf "$plugin_dir/Joycon2Ble.bundle"
cp -R "$bundle" "$plugin_dir/"
echo "Built $plugin_dir/Joycon2Ble.bundle ($(lipo -archs "$bundle/Contents/MacOS/Joycon2Ble"))"
