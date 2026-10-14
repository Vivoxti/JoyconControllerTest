# Two Players sample

1. Create an empty scene, add an empty GameObject, add **TwoPlayersSample**, press Play.
2. Hold **SYNC** on a Joy-Con 2 until its LEDs run (web: then tap **Connect L / R** in the page panel).
3. Press any button on it to join. Stick — move, South — jump, tilt — rotate, Select (Capture / Home) — recenter.

Any other gamepad (or a second Joy-Con) joins as the second player. The script shows the whole integration:
`Gamepad` for input, `Joycon2Gamepad.attitude` for tilt, `Joycon2Manager.SetPlayerIndex` for the LEDs and the
`ControllerConnected` / `ControllerDisconnected` events.
