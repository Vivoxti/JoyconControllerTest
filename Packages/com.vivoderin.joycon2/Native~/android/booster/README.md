# Joy-Con 2 Boost — Android / Chrome experiment

Small foreground-service APK that attaches to **already connected** devices named
`Joy-Con 2 (L)` / `Joy-Con 2 (R)` or matching the service UUID supplied by the game, and calls the public
`BluetoothGatt.requestConnectionPriority(CONNECTION_PRIORITY_HIGH)`.
No scan, pairing, Nintendo commands, input reads, subscriptions or virtual gamepads.
For devices without names, it discovers GATT service metadata before boosting;
devices without the target service are released and skipped for five minutes.
Chrome and the existing C# core continue to handle game input.

## Why this may work

- [Android API](https://developer.android.com/reference/android/bluetooth/BluetoothGatt#requestConnectionPriority(int))
  exposes a connection-parameter request to native apps; Web Bluetooth does not.
- [AOSP Android 12 intervals](https://android.googlesource.com/platform/packages/apps/Bluetooth/+/refs/tags/android-12.0.0_r1/res/values/config.xml)
  specify BALANCED 24–40 × 1.25 ms (30–50 ms), HIGH 9–12 × 1.25 ms (11.25–15 ms).
  Vendor overlays and negotiation can differ. The observed ~20 Hz is consistent
  with 50 ms. After boosting, the phone reported a 15 ms interval on both links.
- [AOSP native request](https://android.googlesource.com/platform/system/bt/+/refs/tags/android-12.0.0_r1/btif/src/btif_gatt_client.cc)
  calls `BTA_DmBleUpdateConnectionParams` by remote address when the link exists.
  This supports the hypothesis that another GATT client can speed up Chrome's
  shared physical link. The user confirmed 60+ Hz on both in our WebGL game.

## Test

1. Install the helper APK once. In Android game **Details / diagnostics**, use
   **Install boost helper**, or the primary **Boost** button's APK fallback.
2. In the game tap **Boost**. The helper starts the service and finishes its
   activity, returning to Chrome. Nearby devices permission is requested on first
   use. Then SYNC + Connect L/R in the game; the helper attaches as links appear.
   A quick helper hop preserves existing links. A permission/install hop longer
   than two seconds closes them on return, so first use is best before connecting.
3. In game **Details**, compare Hz L/R, report lengths, gyro and game FPS against
   the baseline (~20 Hz L/R with 50–60+ FPS on Pixel 3 / Android 12).
4. Play for at least a minute, then ten minutes. Test adding the second controller,
   disconnect/reconnect and stopping the helper. If needed, read diagnostics:
   `adb logcat -s Joycon2Boost`.

The helper asks HIGH when each device joins (on both handles) and every ten
seconds while attached. The foreground notification has a Stop action; the app
also has **Stop boost**. It stops after thirty minutes. No automatic service restart.
Stop closes only this app's clients; the negotiated interval may remain until a
new link is made. For another baseline, stop the helper and reconnect in Chrome.

`HIGH request accepted=true` only confirms that Android accepted the request,
not that the link changed or reached 60+ Hz. This experiment skips nameless or
renamed devices unless the game's supplied service UUID matches (or addresses
were explicitly configured for development). No root, Shizuku or
hidden APIs are used.

## Build

```bash
Native~/android/booster/build.sh                 # macOS / Linux
```

```powershell
powershell -File Native~/android/booster/build.ps1   # Windows
```

Uses Unity's OpenJDK / Android SDK 36, no Gradle dependencies or Unity rebuild.
Output: `WebHelpers~/Joycon2Boost-android.apk` in the package (debug-signed, min API 26, target 36); WebGL builds
serve it next to index.html. The APK signature must not change between updates of an installed helper: build with the
same key (`JC2_ANDROID_KEYSTORE`) or uninstall the old one first.
Build checks Java, DEX, manifest packaging, zipalign and APK signature.

Optional device configuration for development: launch `.BoostActivity` with the
`controllers` string extra containing comma-separated, validated Bluetooth addresses.
Values are saved in this app's preferences; they are not hardcoded in the source.
Diagnostics are also saved to this app's external files directory as `status.txt`.
Normal play uses `joycon2boost://start?service=<uuid>` via Chrome's `intent://`
anchor with APK fallback. UUID comes from C# request options; it is not hardcoded
in Java. A valid service link clears the old development MAC list.

2026-10-14: v0.1 compiled/installed; the user's Chrome rate remained 20–22 Hz.
The helper service and permission were active, but the system's GATT client list
showed only Chrome. v0.2 adds explicit address selection and exported diagnostics;
installed on Pixel 3 with the two controller addresses observed in its GATT dump.
v0.2 hardware result: both Android and Chrome callbacks report interval=12
(15 ms); user confirmed **60+ Hz on both**. v0.3 adds browser launch, automatic
return and service verification instead of manual addresses. Both devices passed
service verification in the helper log; the user confirmed the Boost-button flow,
automatic return to Chrome and 60+ Hz on both. Ten-minute stability remains untested.
