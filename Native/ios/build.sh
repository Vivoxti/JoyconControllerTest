#!/bin/bash
# Builds libJoycon2Ble.a (iOS device, arm64) into Assets/Joycon2/Plugins/iOS/ from the macOS CoreBluetooth source
# (Native/macos/src/joycon2ble.m): CoreBluetooth has the same API on both. Needs Xcode with the iOS SDK.
# The simulator has no Bluetooth, so there is no simulator slice. Unity links the archive into the Xcode project
# (the plugin importer adds CoreBluetooth.framework); C# imports it with DllImport("__Internal").
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/build"
plugin_dir="$here/../../Assets/Joycon2/Plugins/iOS"
min_os=15.0

rm -rf "$out"
mkdir -p "$out" "$plugin_dir"

xcrun -sdk iphoneos clang -arch arm64 -miphoneos-version-min=$min_os -fobjc-arc -O2 -Wall -Wextra -Werror \
      -fvisibility=hidden -I "$here/../windows/include" \
      -c "$here/../macos/src/joycon2ble.m" -o "$out/joycon2ble.o"
xcrun -sdk iphoneos libtool -static -o "$out/libJoycon2Ble.a" "$out/joycon2ble.o"

cp "$out/libJoycon2Ble.a" "$plugin_dir/"
echo "Built $plugin_dir/libJoycon2Ble.a ($(lipo -archs "$out/libJoycon2Ble.a"))"
nm -gU "$out/libJoycon2Ble.a" | grep -c " _jc2_" | xargs echo "exported jc2_ functions:"
