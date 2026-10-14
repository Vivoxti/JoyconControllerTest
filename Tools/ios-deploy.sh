#!/bin/bash
# Signs the Unity-generated Xcode project (Kagefumi → Build iOS Xcode project), installs the app on the connected
# iPhone and launches it. Needs Xcode signed in to the team in KagefumiIOSBuild (automatic signing).
#   Tools/ios-deploy.sh                  build + install + launch
#   Tools/ios-deploy.sh --console        … and stream the app's stdout/stderr (Unity log) until Ctrl+C
#   JC2_IOS_DEVICE=<udid|name>           pick the device (default: the first connected iPhone)
#   JC2_IOS_TEAM=<team id>               signing team (default: the one in Player Settings → iOS)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
project_root="$here/.."
xcode="$project_root/Builds/iOS/Xcode"
derived="$project_root/Builds/iOS/DerivedData"
team="${JC2_IOS_TEAM:-$(awk '/appleDeveloperTeamID:/ { print $2; exit }' "$project_root/ProjectSettings/ProjectSettings.asset")}"
[ -n "$team" ] || { echo "No signing team: set JC2_IOS_TEAM or Player Settings → iOS → Signing Team ID" >&2; exit 1; }

[ -d "$xcode/Unity-iPhone.xcodeproj" ] || { echo "No $xcode — run Kagefumi → Build iOS Xcode project first" >&2; exit 1; }

device="${JC2_IOS_DEVICE:-}"
if [ -z "$device" ]; then
    device=$(xcrun devicectl list devices --hide-default-columns --columns Identifier --columns State --columns Model 2>/dev/null |
             awk '/(connected|available)/ && /iPhone|iPad/ { print $1; exit }')
fi
[ -n "$device" ] || { echo "No connected iPhone (xcrun devicectl list devices)" >&2; exit 1; }

xcodebuild -project "$xcode/Unity-iPhone.xcodeproj" -scheme Unity-iPhone -configuration Debug \
           -destination "generic/platform=iOS" -derivedDataPath "$derived" -allowProvisioningUpdates \
           DEVELOPMENT_TEAM="$team" CODE_SIGN_STYLE=Automatic build -quiet

app=$(ls -d "$derived"/Build/Products/Debug-iphoneos/*.app | head -1)
bundle_id=$(/usr/libexec/PlistBuddy -c "Print CFBundleIdentifier" "$app/Info.plist")
echo "Installing $(basename "$app") ($bundle_id) on $device"
xcrun devicectl device install app --device "$device" "$app"

if [ "${1:-}" = "--console" ]; then
    xcrun devicectl device process launch --device "$device" --terminate-existing --console "$bundle_id"
else
    xcrun devicectl device process launch --device "$device" --terminate-existing "$bundle_id"
fi
