#!/bin/bash
# Builds the Android web rate helper into the package's WebHelpers~/Joycon2Boost-android.apk (macOS / Linux;
# build.ps1 does the same on Windows), signed with the Android debug key. WebGL builds serve it next to index.html.
#   UNITY_ANDROID=/path/to/PlaybackEngines/AndroidPlayer ./build.sh     (default: the Unity 6000.3.18f1 Hub install)
#   JC2_ANDROID_KEYSTORE=… JC2_ANDROID_KEY_ALIAS=… JC2_ANDROID_KEY_PASS=…  sign with your own key
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
unity_android="${UNITY_ANDROID:-/Applications/Unity/Hub/Editor/6000.3.18f1/PlaybackEngines/AndroidPlayer}"
jdk="$unity_android/OpenJDK/bin"
tools="$unity_android/SDK/build-tools/36.0.0"
platform="$unity_android/SDK/platforms/android-36/android.jar"
keystore="${JC2_ANDROID_KEYSTORE:-$HOME/.android/debug.keystore}"
alias="${JC2_ANDROID_KEY_ALIAS:-androiddebugkey}"
pass="${JC2_ANDROID_KEY_PASS:-android}"
for f in "$jdk/javac" "$tools/aapt" "$tools/zipalign" "$platform" "$keystore"; do
    [ -e "$f" ] || { echo "Missing: $f" >&2; exit 1; }
done

out="$here/build"
rm -rf "$out"
mkdir -p "$out/classes" "$out/dex"
find "$here/src" -name '*.java' > "$out/sources.txt"
"$jdk/javac" -source 8 -target 8 -Xlint:-options -classpath "$platform" -d "$out/classes" @"$out/sources.txt"
"$jdk/jar" cf "$out/classes.jar" -C "$out/classes" .
"$jdk/java" -cp "$tools/lib/d8.jar" com.android.tools.r8.D8 --lib "$platform" --min-api 26 --output "$out/dex" "$out/classes.jar"
"$tools/aapt" package -f -M "$here/AndroidManifest.xml" -I "$platform" -F "$out/unsigned.apk"
(cd "$out/dex" && "$tools/aapt" add "$out/unsigned.apk" classes.dex)
"$tools/zipalign" -f 4 "$out/unsigned.apk" "$out/aligned.apk"

apk="$here/../../../WebHelpers~/Joycon2Boost-android.apk"
mkdir -p "$(dirname "$apk")"
"$jdk/java" -jar "$tools/lib/apksigner.jar" sign --ks "$keystore" --ks-key-alias "$alias" --ks-pass "pass:$pass" \
    --key-pass "pass:$pass" --v4-signing-enabled false --out "$apk" "$out/aligned.apk"
"$jdk/java" -jar "$tools/lib/apksigner.jar" verify "$apk"
"$tools/aapt" dump badging "$apk" | head -1
echo "Built $(cd "$(dirname "$apk")" && pwd)/$(basename "$apk")"
