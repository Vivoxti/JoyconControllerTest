# Joy-Con 2 Input (`com.vivoderin.joycon2`)

Joy-Con 2 (Nintendo Switch 2) по Bluetooth LE как обычные геймпады Unity Input System: **один Joy-Con, который держат
боком, — один игрок**. Кнопки, стик, гироскоп, акселерометр и готовая поза (`attitude`) на Windows, macOS, Android, iOS
и в WebGL (Chrome/Edge, Bluefy на iOS). Нативные BLE-плагины в комплекте, ничего ставить отдельно не нужно.

Joy-Con 2 — это не HID, а BLE с собственным GATT-сервисом Nintendo: ни ОС, ни Input System, ни Gamepad API браузера
их сами не видят. Пакет подключается к ним напрямую и добавляет в Input System устройство `Joycon2Gamepad : Gamepad`,
поэтому все биндинги `<Gamepad>/…` работают без изменений.

## Установка

**Package Manager → + → Add package from git URL…**

```
https://github.com/Vivoxti/JoyconControllerTest.git?path=/Packages/com.vivoderin.joycon2#v1.0.0
```

или в `Packages/manifest.json`:

```json
"com.vivoderin.joycon2": "https://github.com/Vivoxti/JoyconControllerTest.git?path=/Packages/com.vivoderin.joycon2#v1.0.0"
```

Требования: Unity 6000.0+, Input System 1.19+ (подтянется зависимостью), **Active Input Handling = Input System
Package (New) или Both**.

## Быстрый старт

1. Установить пакет. Больше ничего делать не нужно: менеджер стартует сам до загрузки первой сцены.
2. Зажать **SYNC** на Joy-Con 2 (маленькая кнопка на рельсе) до бегущих огоньков. В браузере после этого нажать
   **Connect L / R** во встроенной панели и выбрать контроллер в списке Chrome.
3. Joy-Con появляется как `Gamepad`: `PlayerInput`, `InputAction` с биндингами `<Gamepad>/…`, `Gamepad.all` — всё
   работает как с любым геймпадом.

Готовая мини-сцена — **Package Manager → Joy-Con 2 Input → Samples → Two Players**.

### Раскладка «боком»

| Gamepad | Joy-Con 2 (L) боком | Joy-Con 2 (R) боком |
|---|---|---|
| `leftStick` | стик (повёрнут на 90°) | стик (повёрнут на 90°) |
| `buttonSouth` / `East` / `North` / `West` | ← / ↓ / → / ↑ | A / X / Y / B |
| `leftShoulder` / `rightShoulder` | SL / SR | SL / SR |
| `leftTrigger` / `rightTrigger` | L / ZL | R / ZR |
| `startButton` / `selectButton` | − / Capture | + / Home |
| `leftStickButton` | нажатие стика | нажатие стика |

Плюс `<Joycon2Gamepad>/angularVelocity` (°/с), `/acceleration` (g) и `/attitude` (поза относительно нуля). Оси —
в системе «боком»: X вправо, Y вверх из лицевой стороны, Z вперёд к рельсе SL/SR.

## API

```csharp
using Joycon2;
using UnityEngine.InputSystem;

// Ввод — как с любым геймпадом.
Vector2 move = gamepad.leftStick.ReadValue();

// Игрок занял слот → зажечь его номер на Joy-Con (для других устройств ничего не делает).
Joycon2Manager.SetPlayerIndex(gamepad, playerIndex);

// Наклон — только у Joy-Con 2.
if (gamepad is Joycon2Gamepad joycon)
{
    Quaternion tilt = joycon.attitude.ReadValue(); // identity = как в момент обнуления
    joycon.Recenter();                             // обнулить позу (Select делает то же самое)
    joycon.AutoCenter = false;                     // пока поза что-то рулит — не подтягивать ноль
    float volts = joycon.BatteryVoltage;
}

// События подключения (главный поток).
Joycon2Manager.ControllerConnected += c => Debug.Log($"{c.Name} ready, gamepad {c.Device}");
Joycon2Manager.ControllerDisconnected += (c, reason) => Debug.Log($"{c.Name} gone: {reason}");
```

| Тип | Зачем |
|---|---|
| `Joycon2Gamepad` | Устройство Input System: `Gamepad` + `angularVelocity`, `acceleration`, `attitude`; `PlayerIndex`, `Recenter()`, `AutoCenter`, `BatteryVoltage`, `ReportRate`, `Joycon2Gamepad.All` |
| `Joycon2Manager` | Подключения: `Controllers`, `ControllerConnected/Disconnected`, `SetPlayerIndex`, `Disconnect`, `Status`, `Settings`; веб — `RequestController`, `CanRequestController`, `ConnectsOnRequest` |
| `Joycon2Lifecycle` | Платформенный старт (разрешения Android, пауза iOS, встроенный UI): `Start/Stop`, и всё для своего UI — `Status`, `Retry()`, `SettingsShortcuts` |
| `Joycon2Controller` | Одно BLE-соединение: сторона, сессия, MTU, Гц, калибровка, поза |
| `Joycon2RateBooster` | Веб: помощники, поднимающие частоту браузера (см. ниже) |
| `Joycon2Protocol`, `Joycon2Report`, `Joycon2Session` | Протокол без UnityEngine — для своих транспортов и тестов |

## Настройки

**Edit → Project Settings → Joy-Con 2** (или **Tools → Joy-Con 2 → Project Settings**). Без ассета действуют значения
по умолчанию. Ассет создаётся кнопкой в `Assets/Settings/Resources/Joycon2Settings.asset` (годится любая папка
`Resources`). В рантайме `Joycon2Manager.Settings` — копия: её можно менять из кода, ассет не трогается.

| Настройка | По умолчанию | |
|---|---|---|
| Auto Start | вкл. | Старт до первой сцены. Выкл. — вызвать `Joycon2Lifecycle.Start()` самому |
| Max Controllers | 2 | Сколько Joy-Con подключать (скан останавливается: он тормозит живые соединения) |
| Pairing Mode Only | вкл. | Подключать только Joy-Con с зажатым SYNC |
| Connect Timeout / Retry Cooldown | 20 с / 3 с | |
| Report Silence Timeout | 3 с | Тишина дольше — соединение потеряно, даже если ОС промолчала (сброс Bluetooth, Bluefy). 0 = выкл. |
| Recenter On Select | вкл. | Select обнуляет `attitude` |
| Diagnostics Overlay | Development Builds | Оверлей F9 (кнопки, стики, IMU, Гц) и запись сырых репортов F10 |
| Mobile Connection Panel | вкл. | Android/iOS: полоска со статусом, Connect / retry, кнопками настроек |
| Web Connection Panel | вкл. | WebGL: HTML-панель Connect L / R, статус, диагностика |
| Mobile Target Frame Rate | 60 | Android/iOS по умолчанию 30 FPS, а репорты идут ~66 Гц. 0 = не трогать |
| Refresh Slow Links | вкл. | Веб + macOS + joycon2boost: пересоздать «медленное» соединение (нужен SYNC). Можно выключать на время раунда |
| Copy Rate Helpers To Web Build | вкл. | Положить помощники рядом с `index.html` |
| Bluetooth Usage Description | «Joy-Con 2 controllers connect over Bluetooth.» | Текст запроса Bluetooth на iOS/macOS |

Поменять настройки из кода до старта:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
static void ConfigureJoycons() => Joycon2Manager.Settings.MaxControllers = 4;
```

## Платформы

| Платформа | Транспорт | Частота | Что важно |
|---|---|---|---|
| Windows 10/11 (Editor + Standalone x64) | WinRT, `Joycon2Ble.dll` | ~67 Гц | Не спаривать в «Параметрах Windows». Editor держит DLL до закрытия |
| macOS 11+ (Editor + Standalone, arm64 + x64) | CoreBluetooth, `Joycon2Ble.bundle` | ~66 Гц | При первом запуске macOS спросит доступ к Bluetooth |
| Android 6+ (API 23) | `android.bluetooth.le`, `joycon2ble.aar` | 60+ Гц | Разрешение «Устройства поблизости» (на ≤ 11 — геолокация, и она должна быть включена) |
| iOS 15+ (только устройство) | CoreBluetooth, `libJoycon2Ble.a` | 60+ Гц | Симулятора нет: в нём нет Bluetooth, слайса под него в библиотеке нет |
| WebGL — Chrome/Edge (Windows, macOS, Android) | Web Bluetooth | 17 / 33 / 20 Гц, с помощником ~66 | Только HTTPS или `localhost`. Контроллер выбирают в списке браузера |
| WebGL — iOS/iPadOS | Web Bluetooth в **Bluefy** | ~30 Гц | Safari и остальные браузеры iOS не умеют Web Bluetooth; панель предложит открыть в Bluefy |

Не поддерживаются: Linux, Safari, Firefox.

### Что сборка делает сама

Build processor пакета:

- **iOS** — добавляет `NSBluetoothAlwaysUsageDescription` в `Info.plist`. Без него iOS убивает приложение при первом
  обращении к Bluetooth, а поля в Player Settings нет. Свой текст игры не перезаписывается. В development-билдах
  записи F10 видны в приложении «Файлы».
- **macOS** — заполняет Player Settings → Bluetooth Usage Description, если поле пустое.
- **IL2CPP** — сохраняет сборку `Joycon2.Runtime` от стриппинга (layout Input System ищется рефлексией).
- **WebGL** — копирует помощники частоты из `WebHelpers~` рядом с `index.html`.
- **Android** — разрешения Bluetooth уже лежат в манифесте AAR и вливаются в манифест игры.

### WebGL

- **Жест пользователя.** Браузер открывает список Bluetooth только сразу после клика или клавиши; кнопки геймпада
  не считаются. Встроенная панель (Connect L / R) открывает список прямо из обработчика тапа. Со своим UI — вызвать
  `Joycon2Manager.RequestController(side)` в кадре клика (если браузер всё же откажет, `.jslib` откроет список на
  следующий клик по странице).
- **Шаблон.** Панель монтируется рядом с canvas (`canvas.parentElement`). Если шаблон разворачивает на весь экран
  только canvas, панель в полноэкранном режиме не видна — разворачивайте контейнер. Страница во `<iframe>` должна
  разрешать `allow="bluetooth"`.
- **Фон.** На телефонах уход страницы в фон закрывает соединения: после возврата нужен SYNC и Connect. На десктопе
  фоновая вкладка соединения держит.
- **Помощники частоты** (`Joycon2RateBooster`). Браузеры не просят у ОС короткий интервал соединения. Маленькие
  помощники просят его за них: `Joycon2Boost.exe` (Windows), `joycon2boost.app` (macOS), `Joycon2Boost-android.apk`
  (кнопка Boost в панели). Первый раз их запускают руками, дальше игра запускает их по ссылке `joycon2boost://`.
- Локальный сервер для проверки: `node Packages/com.vivoderin.joycon2/Native~/web/serve.cjs 8765 Builds/WebGL`.

## Пересборка нативных плагинов

Исходники — в `Native~/` (Unity их не импортирует), бинарники — в `Runtime/Plugins/`, помощники — в `WebHelpers~/`.

| Что | Скрипт | Где собирать |
|---|---|---|
| `Joycon2Ble.dll` + `Joycon2Boost.exe` | `Native~/windows/build.ps1` | Windows, VS 2022 C++ + CMake/Ninja. **Закрыть Unity** (DLL залочена) |
| `Joycon2Ble.bundle` + `Joycon2Boost-mac.zip` | `Native~/macos/build.sh` | macOS, Xcode CLT. После Play Mode закрыть Unity. `JC2_MAC_SIGN_IDENTITY` — подпись |
| `libJoycon2Ble.a` | `Native~/ios/build.sh` | macOS, Xcode с iOS SDK |
| `joycon2ble.aar` | `Native~/android/build.sh` / `build.ps1` | Любая ОС с модулем Android у Unity |
| `Joycon2Boost-android.apk` | `Native~/android/booster/build.sh` / `build.ps1` | То же; подпись — debug-ключ или `JC2_ANDROID_KEYSTORE` |

Все плагины реализуют один C ABI (`Native~/windows/include/joycon2ble.h`): скан → connect → subscribe → write, события
в очереди, C# забирает их раз в кадр. Протокол Nintendo — только в C#.

## Тесты

- EditMode (Test Runner → `Joycon2.Tests.Editor`): протокол, парсер, калибровка, handshake, поза, контроллер,
  `Joycon2Gamepad` в Input System, фильтры браузера, Info.plist. В проекте игры тесты пакета видны, если добавить
  `"testables": ["com.vivoderin.joycon2"]` в `Packages/manifest.json`.
- WebGL-мост без браузера и железа: `node --test Native~/web/tests/web-controls.test.cjs` (из папки пакета).

## Ограничения

- Каждый запуск — снова SYNC: привязку к хосту (команда `0x15`) пакет не делает. В вебе она невозможна.
- Режим «L + R как один геймпад», HD-вибрация, NFC, ИК, «мышь» Joy-Con 2 — не реализованы.
- macOS/iOS: ~66 Гц достигаются приватными опциями CoreBluetooth. Для App Store это риск ревью, без них будет ~33 Гц.
  На macOS опции отключает переменная окружения `JC2_MAC_CI_FRAMES=0`.
- Windows: только x64 (ARM64-слайса нет).
- `Joycon2Ble.bundle` подписан ad hoc. Нотаризуемая игра переподписывает `.app` целиком (`codesign --deep`) или
  собирает бандл с `JC2_MAC_SIGN_IDENTITY`.

## Документация

`Documentation~/`: [PROTOCOL.md](Documentation~/PROTOCOL.md) — протокол (UUID, команды, разметка репорта);
[PLAN.md](Documentation~/PLAN.md) — архитектура и история платформ; [TEST_LOG.md](Documentation~/TEST_LOG.md) —
проверки на железе; исследования по Android.
