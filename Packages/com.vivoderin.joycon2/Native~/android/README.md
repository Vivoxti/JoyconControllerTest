# Android BLE byte transport

Dependency-free Java AAR. Generic scan/connect/subscribe/write: Nintendo UUIDs and handshake live in C#.
Source is original project code; no third-party Joy-Con implementation has been copied.

## Build

```bash
Native~/android/build.sh                                   # macOS / Linux
UNITY_ANDROID=/path/to/PlaybackEngines/AndroidPlayer Native~/android/build.sh
```

```powershell
./Native~/android/build.ps1                                # Windows
./Native~/android/build.ps1 -UnityAndroid 'D:\Unity\Editor\Data\PlaybackEngines\AndroidPlayer'
```

Uses Unity's OpenJDK and SDK android-36/android.jar. The classes target Java 8; runtime branches support
Android API 23+ and use the API 33 immutable-value write/notification overloads where available.
The result is `Runtime/Plugins/Android/joycon2ble.aar` (package folder), including Bluetooth permissions and
JNI keep rules. No Gradle dependency download or NDK build is needed for the AAR.

In the KAGEFUMI test project, **Kagefumi → Build Android APK (Joy-Con dev)** builds
`Builds/Android/Kagefumi-Joycon2-dev.apk` (ARM64, IL2CPP, Development, landscape) and
`Tools/android-verify-apk.ps1` verifies its signature, merged permissions and the Java BLE class.

## Event ABI

`pollEvent()` returns null, or a Java `byte[]`: a 32-byte little-endian header followed by payload.
Header fields match `Joycon2TransportEvent`: type, handle, tag, status (four int32), address (int64),
RSSI and payload length (two int32). C# uses `sbyte[]` across JNI and copies bytes without sign conversion.

Manufacturer payload excludes the 2-byte company id. GATT payload is untouched (no prefix removal).
`Connected` means MTU and service discovery are complete. `Subscribed` follows the CCCD callback.
The minimum acceptable MTU is passed from C# (currently 92 to fit its calibration responses).

One HandlerThread owns all Android objects. Every handle has its own operation queue. Callbacks
are marshalled to the worker; polling only locks the bounded event queue. An operation timeout closes
its GATT instance so a late callback cannot complete the next operation. Old GATT callbacks are ignored.
Both links request public HIGH connection priority, including after another device joins.
No bonding, private API, system input injection or foreground service.

Status codes: Android GATT/scan codes are retained; local errors are -1001 permission, -1002 Bluetooth
disabled, -1003 operation failure, -1004 timeout/cooldown (the periodic 30 s scan cycle — C# restarts quietly),
-1005 MTU insufficient/unavailable, -1006 Location off (API 30 and older scan nothing without it).
Use logcat tags `Joycon2Ble` and `Unity` to distinguish transport from the C# session.

## Phone check

1. Install the APK; allow **Nearby devices** (older Android: Location). Bluetooth must be enabled.
2. Hold SYNC on each original Joy-Con 2. Do not bond it from Android Bluetooth settings.
3. Tap **Details**: each side should become a separate gamepad; check MTU, packet length, calibration,
   frequency, stick, buttons, gyro and player LEDs. Press a controller button to join the game.
4. Tap **Record** / **Stop recording** to save raw bytes under the app's `persistentDataPath/Joycon2Dumps`.
5. Test 10 minutes, Bluetooth off/on, controller off/on, and app background/return.
   Returning to the app restarts scanning; hold SYNC again. **Connect / retry** restarts both links.

Diagnostic APK is debuggable. On an authorized USB phone:

```powershell
adb install -r Builds/Android/Kagefumi-Joycon2-dev.apk
adb logcat -s Unity Joycon2Ble
adb shell run-as com.vivoderin.kagefumi ls -l files/Joycon2Dumps
```

Dump storage can be external on Android; the exact absolute path is printed to the Unity log when
recording starts. Use that path when retrieving a file. Background play is intentionally unsupported:
the transport closes when Unity pauses, then starts again on resume with SYNC.

Implementation/build verification does not establish hardware compatibility; record actual phone
model, Android version, Joy-Con firmware, MTU and frequencies in `Documentation~/TEST_LOG.md`.
