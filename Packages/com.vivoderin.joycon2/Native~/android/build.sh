#!/bin/bash
# Builds joycon2ble.aar into the package's Runtime/Plugins/Android/ (macOS / Linux; build.ps1 does the same on Windows).
# Uses the OpenJDK and SDK that ship with Unity's Android module: no Gradle, no NDK.
#   UNITY_ANDROID=/path/to/PlaybackEngines/AndroidPlayer ./build.sh     (default: the Unity 6000.3.18f1 Hub install)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
unity_android="${UNITY_ANDROID:-/Applications/Unity/Hub/Editor/6000.3.18f1/PlaybackEngines/AndroidPlayer}"
jdk="$unity_android/OpenJDK/bin"
android_jar="$unity_android/SDK/platforms/android-36/android.jar"
for tool in "$jdk/javac" "$jdk/jar" "$android_jar"; do
    [ -e "$tool" ] || { echo "Missing Android build tool: $tool (set UNITY_ANDROID)" >&2; exit 1; }
done

out="$here/build"
rm -rf "$out"
mkdir -p "$out/classes" "$out/aar"
find "$here/src" -name '*.java' > "$out/sources.txt"
"$jdk/javac" -source 8 -target 8 -Xlint:deprecation -Xlint:-options -classpath "$android_jar" -d "$out/classes" @"$out/sources.txt"
"$jdk/jar" cf "$out/aar/classes.jar" -C "$out/classes" .
cp "$here/AndroidManifest.xml" "$out/aar/"
cp "$here/consumer-rules.pro" "$out/aar/proguard.txt"

aar="$here/../../Runtime/Plugins/Android/joycon2ble.aar"
mkdir -p "$(dirname "$aar")"
# jar writes a ZIP-compatible AAR with a plain XML manifest.
"$jdk/jar" cf "$aar" -C "$out/aar" .
echo "Built $aar ($(wc -c < "$aar" | tr -d ' ') bytes)"
