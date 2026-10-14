# Changelog

Формат — [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии — [SemVer](https://semver.org/lang/ru/).

## [1.0.0] — 2026-10-14

Первая версия пакета: код из тестового проекта KAGEFUMI (`Assets/Joycon2`, `Native/`, `Docs/Joycon2`) собран в UPM-пакет.

### Добавлено
- Project Settings → Joy-Con 2 (`Joycon2Settings`): автостарт, число контроллеров, таймауты, встроенный UI, частота кадров
  на телефонах, текст запроса Bluetooth. В рантайме — копия (`Joycon2Manager.Settings`), ассет не меняется.
- `Joycon2Lifecycle`: платформенный старт (разрешения Android, пауза iOS) и API для своего UI подключения — `Status`,
  `Retry()`, `SettingsShortcuts`.
- События `Joycon2Manager.ControllerConnected` / `ControllerDisconnected`, `FindController(device)`.
- `Joycon2Gamepad`: `PlayerIndex`, `Recenter()`, `AutoCenter`, `BatteryVoltage`, `ReportRate`, `All`.
- Build processor: `NSBluetoothAlwaysUsageDescription` в Info.plist (iOS), Bluetooth Usage Description (macOS),
  link.xml для IL2CPP, помощники частоты рядом с `index.html` (WebGL). Раньше это делали меню сборки KAGEFUMI.
- `build.sh` для Android AAR и Android-помощника — сборка на macOS/Linux.
- Sample «Two Players», README. Тесты: 20 новых EditMode (63 всего; менеджер целиком — на фейковом транспорте) и 6 новых
  Node-тестов веб-моста (18 всего).

### Изменено
- Встроенный UI опционален: оверлей F9 по умолчанию только в Editor/development (раньше создавался и в релизе),
  мобильная панель и веб-панель отключаются в настройках. `targetFrameRate` — настройка.
- Сторож тишины (нет репортов дольше `ReportSilenceTimeout` → соединение потеряно) работает на всех платформах, не
  только в вебе.
- `Joycon2Manager` разделён: `Joycon2Controller` (обработка репорта и IMU), `Joycon2Manager.Web` (выбор в браузере,
  пересоздание медленных соединений). Публичные статические поля-настройки перенесены в `Joycon2Settings`.
- Android AAR: `minSdkVersion` 25 → 23 (не ломает merge манифеста в играх с минимумом по умолчанию).
- Android-помощник: `Joycon2Boost-test.apk` → `Joycon2Boost-android.apk` в `WebHelpers~`.
- Бандл macOS: идентификатор `com.vivoderin.joycon2.ble`, подпись через `JC2_MAC_SIGN_IDENTITY`.

### Исправлено
- macOS/iOS: после сброса Bluetooth (перезапуск bluetoothd, сон/пробуждение) геймпады оставались с замёрзшим вводом —
  CoreBluetooth в этом состоянии не сообщает об обрыве.
- WebGL: повисшая GATT-операция (Bluefy) навсегда блокировала контроллер, в том числе после переподключения —
  таймаут 8 с и сброс очереди операций при connect.
- WebGL: переподключение во время ретрая поиска сервиса оставляло listener старого соединения, и ввод нового
  соединения отбрасывался.
- WebGL: на десктопе уход вкладки в фон рвал все соединения (теперь — только на телефонах).
- WebGL: пока список ждал «свежего» клика, клик по своей же панели (например, Connect R) открывал запрос L.
- WebGL: ядро не зависит от `.jslib` панели (`jc2web_is_picking` перенесён в транспорт).
- Android: на API ≤ 30 с выключенной геолокацией скан молча ничего не находил — теперь понятная ошибка.
- Android: ложное предупреждение «scan stopped (error -1004)» каждые ~35 с, пока ждали второй Joy-Con.
- Windows: исключение на одном шаге закрытия устройства пропускало остальные (`MaintainConnection` оставался
  включён). Неудачная подписка оставляла обработчик, и повторная подписка дублировала ввод.
  **Исходники исправлены, DLL нужно пересобрать на Windows** (`Native~/windows/build.ps1`).
- Запись в нативный транспорт проверяет длину буфера.
- Обработчик `ControllerDisconnected`, вызывающий `Disconnect`/`Start`, не ломает цикл опроса и `Stop()`.
- После паузы приложения (iOS в фоне 3–5 с) сторож тишины не считает соединения потерянными.
- `Joycon2Manager.Start()` вне Play Mode больше не падает на `DontDestroyOnLoad`.
- WebGL без встроенной панели: запись F10 скачивается сразу.
