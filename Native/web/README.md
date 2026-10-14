# Android Chrome / WebGL

Build in Unity: **Kagefumi → Build WebGL (dev, localhost)**.
Output: `Builds/WebGL`, responsive template `Assets/WebGLTemplates/Kagefumi`.

For a phone, upload the **entire contents** of `Builds/WebGL` to trusted HTTPS
hosting and open `index.html` directly in Chrome. No Android APK/AAR is needed.
HTTP over the computer's LAN IP does not grant Web Bluetooth access.

Local development over USB, from the repository root:

```powershell
node Native/web/serve.cjs
```

In a second terminal, with a phone that already allows USB debugging:

```powershell
& 'C:/Program Files/Unity/Hub/Editor/6000.3.18f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe' reverse tcp:8765 tcp:8765
```

On the phone, open **http://localhost:8765** in Chrome. The server listens only on
the computer's loopback interface; the USB reverse makes that origin available
on the phone. No browser flags or certificate bypasses are needed.

On Android, tap **Boost** for 60+ Hz. First use requires installing the helper
APK (automatic fallback, or **Details → Install boost helper**) and allowing its
Nearby devices permission. On subsequent uses Boost starts it and returns to the
game automatically. Controller addresses do not need manual configuration.
The helper checks the game's supplied GATT service and uses public HIGH priority.

Hold SYNC on L, tap **Connect L**, select it and allow Chrome's Nearby devices
permission if asked. Repeat with R via a separate tap. Press a controller button
to join P1/P2. Stop any APK or Switch connection using those controllers first.

**Details / diagnostics** shows individual Hz, report lengths and gyro status.
**Record → Stop recording → Download recording** exports raw L/R reports as TSV.
Leaving Chrome for another app closes links (except while its chooser is open);
return, hold SYNC and connect again. Reload also needs another selection.

Checks without hardware:

```powershell
node --test Native/web/tests/web-controls.test.cjs
```

Pixel 3 / Android 12: two Joy-Con in Chrome were ~20 Hz without the helper and
60+ Hz with v0.2; Android reports 15 ms on both shared links. The v0.3 Boost-button
flow and service matching were also confirmed on this phone, with 60+ Hz retained.
SUN GRAB and ten-minute stability still need separate phone checks.
Record results in `Docs/Joycon2/TEST_LOG.md`.

## iPhone / iPad (Bluefy)

Safari and every other iOS browser are WebKit without Web Bluetooth; use the free
**Bluefy** browser (in Safari the game offers **Open in Bluefy** / **Get Bluefy**).
Bluefy needs HTTPS and iOS has no `adb reverse`, so for a local build use a tunnel:

```bash
node Native/web/serve.cjs 8765
cloudflared tunnel --url http://localhost:8765
```

Open the printed `https://….trycloudflare.com` address in Bluefy. Hold SYNC, tap
**Connect L** / **Connect R**. Joy-Con 2 advertise no name, so the list may be empty
or show "DeviceName": close it and tap Connect again to list all Bluetooth devices.
Expect ~30 Hz per controller (iOS default 30 ms interval; the web cannot change it).
The native iOS build reaches 60+ Hz. iPhone 13 mini / iOS 18.7.7: works, ~30 Hz.
