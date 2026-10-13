# Joy-Con 2 как два независимых геймпада — план

> Цель проекта: проверить, можно ли подключить к Unity-игре **два Joy-Con 2 (Nintendo Switch 2)**
> и использовать их как **два отдельных геймпада** (по одному на игрока, хват «боком»),
> на нескольких платформах — нативно и в WebGL-билде.
>
> Тестовая игра: **KAGEFUMI** (`Assets/Kagefumi/`), дуэль на двоих. Она уже раздаёт устройства
> игрокам через `InputSystem.onAnyButtonPress` и схему `"Gamepad"` — см.
> `Assets/Kagefumi/Scripts/KagefumiGame.cs` (`OnAnyButtonPress`, `Join`).
>
> Протокол (UUID, команды, разметка репорта): [PROTOCOL.md](PROTOCOL.md).

Последнее обновление: 2026-10-13 (Фазы 1, A, B, C, D — работают на железе; C — 66 Гц с `joycon2boost.app`; D — частота поднята приватными опциями CoreBluetooth; F — нативный APK работает на Pixel 3 / Android 12, подтверждено пользователем; Гц и длительный прогон ещё не записаны). Unity 6000.3.18f1, Input System 1.19.0.

---

## 1. Ключевой факт, от которого всё строится

Joy-Con 2 — **не** классический Bluetooth HID, как Joy-Con 1. Это **Bluetooth Low Energy** с
проприетарным GATT-сервисом Nintendo. Следствия:

- ОС **не** видит их как геймпад: ни Windows, ни macOS, ни Android не спаривают их через системные
  настройки (а попытка SMP-pairing заставляет контроллер рвать связь).
- Unity Input System, SDL (mainline), браузерный Gamepad API — **ничего из этого Joy-Con 2 не видит**.
- Значит, на каждой платформе нужен **свой BLE-транспорт**, который сам: сканирует → подключается
  → шлёт handshake-команды → подписывается на нотификации → получает сырые 63-байтные репорты.
- Хорошая новость: протокол одинаков везде, и вся «умная» часть (handshake, парсинг, калибровка,
  маппинг) может жить в **одном общем C#-коде**. Платформенный слой — тонкий «трубопровод для байтов».

Протокол реверснут сообществом и подтверждён рабочими проектами на всех нужных нам платформах
(Windows, macOS, Android, Web Bluetooth в Chrome/Edge и Bluefy на iOS) — см. §9.

---

## 2. Матрица платформ и приоритеты

| # | Таргет | BLE-транспорт | Реализация в Unity | Приоритет |
|---|---|---|---|---|
| A | **Windows** Standalone + Editor | WinRT `Windows.Devices.Bluetooth` | нативный C++/WinRT DLL (x64), C ABI | P1 |
| B | **WebGL** в Chrome/Edge на Windows | Web Bluetooth | `.jslib` | P1 |
| C | **WebGL** в Chrome/Edge на macOS | Web Bluetooth | тот же `.jslib` | P1 |
| D | **macOS** Standalone + Editor | CoreBluetooth | нативный `.bundle` (Obj-C, universal arm64+x64) | P1 |
| E | **WebGL** в Chrome на Android | Web Bluetooth | тот же `.jslib` | P2 |
| F | **Android** нативно | `android.bluetooth.le` | Java/Kotlin AAR (или `AndroidJavaObject`) | P3 |
| G | **WebGL** на iOS/iPadOS | Web Bluetooth через браузер **Bluefy** | тот же `.jslib` (фильтр по имени) | P4 |

Не поддерживается и не планируется: Safari и Firefox (нет Web Bluetooth; Chrome на iOS — это WebKit,
тоже нет). Для них — понятное сообщение в UI + ссылка «открыть в Bluefy» на iOS.
Бонус «на потом»: нативный iOS почти бесплатен поверх CoreBluetooth-плагина macOS.

Порядок работ: **0 → 1 → A → B/C → D → E → F → G**. Веб идёт рано, потому что дёшев
(один `.jslib` закрывает сразу B, C, E, G) и даёт первую проверку на Mac ещё до нативного плагина.

---

## 3. Архитектура

```
 Joy-Con 2 (L) ─┐                         ┌─ Joy-Con 2 (R)
                └──── BLE GATT (no SMP) ──┘
                            │
 ┌──────────── Транспорт (тонкий, по платформе) ────────────┐
 │ WinRT DLL │ CoreBluetooth bundle │ Android AAR │ WebBT jslib │
 │ scan · connect · write(no-response) · notify → ring buffer │
 └──────────────────────────┬───────────────────────────────┘
                            │  polling из C#, без колбэков через границу
 ┌──────────── Joycon2Core (C#, общий для всех) ────────────┐
 │ handshake state machine · парсер репорта · калибровка     │
 │ стиков · маппинг «боком» · player LED · keep-alive        │
 └──────────────────────────┬───────────────────────────────┘
                            │  InputSystem.QueueStateEvent (в onBeforeUpdate)
 ┌──────────── Joycon2Gamepad : Gamepad (Input System) ─────┐
 │ state = GamepadState → все биндинги <Gamepad>/... работают │
 └──────────────────────────┬───────────────────────────────┘
                            │
                KagefumiGame — без изменений
          (device is Gamepad → схема "Gamepad" → слот игрока)
```

### 3.1 Принципы

1. **Транспорт ничего не знает про Nintendo.** API транспорта — минимальный и одинаковый на всех
   платформах. UUID сервиса/характеристик передаются из C#.
2. **Polling вместо колбэков.** Нативный код складывает нотификации в потокобезопасную очередь,
   C# каждый кадр забирает их в `InputSystem.onBeforeUpdate`. Так не нужны `MonoPInvokeCallback`
   (IL2CPP), `SendMessage` и маршалинг между потоками; `QueueStateEvent` вызывается только с
   главного потока.
3. **Joy-Con = обычный `Gamepad`.** Кастомный layout `Joycon2Gamepad`, наследник `Gamepad`,
   с форматом состояния `GamepadState`. Тогда существующий `KagefumiControls.inputactions` и
   логика лобби работают без единой правки, а `displayName` вида «Joy-Con 2 (L)» попадёт в HUD.
4. **Один Joy-Con — один игрок.** Режим «пара L+R как один геймпад» — не цель (можно добавить позже
   как опцию, это чисто C#-слой).
5. **Сырые данные записываются.** Дебаг-оверлей умеет дампить репорты в файл → из дампов делаются
   фикстуры для EditMode-тестов парсера. Тесты не требуют железа.

### 3.2 Черновик API транспорта

```csharp
public interface IJoycon2Transport
{
    bool IsSupported { get; }                    // напр. WebGL: navigator.bluetooth есть?
    bool RequiresUserGesture { get; }            // WebGL: true (requestDevice только из клика)

    void StartScan();                            // нативные: фоновое сканирование по manufacturer data
    void StopScan();
    void RequestDevice(Joycon2Side? side);       // WebGL: открыть системный chooser

    // Опрос событий (появилось устройство / подключилось / отвалилось / пришли байты).
    bool TryDequeueEvent(out TransportEvent e, byte[] payloadBuffer);

    void Connect(int handle);
    void Disconnect(int handle);
    void Subscribe(int handle, string serviceUuid, string characteristicUuid);
    void WriteWithoutResponse(int handle, string serviceUuid, string characteristicUuid,
                              byte[] data, int length);
}
```

Нативная сторона экспортирует то же самое как `extern "C"` функции (`jc2_start_scan`,
`jc2_poll_event`, …). Для WebGL — `.jslib` с теми же именами; payload копируется в `HEAPU8`.

### 3.3 Планируемая структура в репозитории

```
Assets/Joycon2/
  Runtime/
    Joycon2.Runtime.asmdef
    Core/          Joycon2Protocol.cs, Joycon2Report.cs, Joycon2Session.cs (handshake FSM),
                   Joycon2Calibration.cs, Joycon2Mapping.cs (боковой хват)
    Input/         Joycon2Gamepad.cs (layout), Joycon2Manager.cs (опрос транспорта → устройства)
    Transport/     IJoycon2Transport.cs, NativeTransport.cs (Win + Mac, один C ABI),
                   AndroidTransport.cs, WebBluetoothTransport.cs, NullTransport.cs
    Debug/         Joycon2DebugOverlay.cs (сырые байты, Hz, кнопки, запись дампов)
  Plugins/
    Windows/x86_64/Joycon2Ble.dll
    macOS/Joycon2Ble.bundle
    Android/joycon2ble.aar
    WebGL/Joycon2WebBluetooth.jslib
  Tests/EditMode/  парсер/калибровка/маппинг на записанных фикстурах
Native/            исходники плагинов (вне Assets, чтобы Unity их не импортировал)
  windows/  (C++/WinRT, CMake или .vcxproj)
  macos/    (Xcode/CMake, Obj-C)
  android/  (Gradle-модуль)
Docs/Joycon2/      этот план + PROTOCOL.md + заметки по результатам тестов
```

---

## 4. Фазы

Статусы: ☐ не начато · ◐ в работе · ☑ готово · ✖ заблокировано/отказ

### Фаза 0 — Проверка железа без Unity (gate) ☐

Цель: до написания кода убедиться, что наши конкретные Joy-Con 2, адаптеры и ОС вообще
работают по BLE, и снять открытые вопросы протокола.

- ☐ Windows + Chrome/Edge: открыть демо <https://joy-con-2-web-bluetooth-api-demo.pages.dev/>,
  подключить **оба** Joy-Con одновременно, проверить кнопки.
- ☐ macOS + Chrome: то же демо (Chrome должен получить доступ к Bluetooth в System Settings → Privacy).
- ☐ (опц.) Android + Chrome, iPad/iPhone + Bluefy: то же демо.
- ☐ Windows: дамп GATT-дерева (nRF Connect на телефоне или любой BLE-сканер) — **в каком сервисе
  лежат vibration-характеристики** (нужно для `optionalServices` в Web Bluetooth).
- ☐ Записать фактический список адаптеров/ОС/версий прошивки Joy-Con в `Docs/Joycon2/TEST_LOG.md`.
- ☐ Убедиться, что рядом нет включённой Switch 2 (контроллер может «просыпаться» к консоли).

**Выход:** «да, оба Joy-Con работают по BLE на Win и Mac» или конкретный блокер. Если демо не
работает — дальше не идём, сначала разбираемся с адаптером/прошивкой.

### Фаза 1 — Общее C#-ядро + Input System ◐ (код готов, ждёт железа)

Код: `Assets/Joycon2/Runtime/` (asmdef `Joycon2.Runtime`). `Core/` — чистый C# без UnityEngine
(тестируется и в Unity, и через `dotnet test`).

- ☑ `Joycon2Report`: парсинг 63-байтного репорта (кнопки 32 бит, стики 12-бит упакованные,
  батарея, IMU). См. PROTOCOL.md.
- ☑ `Joycon2Session`: handshake-FSM: подписка на command-response → user-калибровка →
  (factory-калибровка) → player LED → feature mask `0x37` set/enable → подписка на input.
  Ожидание ответа по `[cmd]/[sub]` (+ адрес для чтения памяти), таймаут 1 с × 2 попытки, пропуск
  неответившего шага, «слепой» режим без ответов. Keep-alive LED раз в 1 с.
- ☑ Калибровка стиков (user-слот, fallback factory, fallback 2048 ± 1250) с учётом квирка правого
  Joy-Con. Мёртвую зону не делаем сами — её даёт процессор `stickDeadzone` у `Gamepad.leftStick`.
- ☑ `Joycon2Mapping`: маппинг бокового хвата (таблица ниже) → нейтральное `Joycon2PadState`
  → `GamepadState`. **На железе не проверен.**
- ☑ `Joycon2Gamepad : Gamepad` (layout по интерфейсу `"Joycon2"`, `canRunInBackground`),
  `AddDevice` после handshake, `RemoveDevice` при обрыве.
- ☑ `Joycon2Manager` (static, `RuntimeInitializeOnLoadMethod`): транспорт по платформе, опрос в
  `InputSystem.onBeforeUpdate`, автоконнект к Joy-Con в режиме SYNC, скан только пока подключено
  меньше `MaxControllers` (2), back-off 3 с на адрес, таймаут коннекта 20 с, остановка при выходе из
  Play Mode / перезагрузке домена. LED — наименьший свободный индекс, а Kagefumi при `Join`
  переназначает его по слоту (`Joycon2Manager.SetPlayerIndex`, одна строка в `KagefumiGame.Join`).
- ☑ Дебаг-оверлей (F9) + запись сырых репортов в `persistentDataPath/Joycon2Dumps` (F10).
- ☑ EditMode-тесты ядра (`Assets/Joycon2/Tests/EditMode`, 22 шт.): байты команд сверены с
  захватами сообщества, парсер, калибровка, маппинг, handshake с фейковым линком. Пока на
  синтетике — дополнить фикстурами из дампов с железа.
- ☐ «Фейковый» транспорт, проигрывающий дамп → гонять игру в Editor без железа (`NullTransport` есть).

Маппинг «боком» (Joy-Con держат горизонтально, рельса SL/SR сверху) — **гипотеза, сверить на железе**:

| Gamepad | Joy-Con 2 (L) боком | Joy-Con 2 (R) боком |
|---|---|---|
| leftStick.x | −rawY | +rawY |
| leftStick.y | +rawX | −rawX |
| buttonSouth | D-Left | A |
| buttonEast | D-Down | X |
| buttonNorth | D-Right | Y |
| buttonWest | D-Up | B |
| leftShoulder | SL (L) | SL (R) |
| rightShoulder | SR (L) | SR (R) |
| leftTrigger / rightTrigger | L / ZL | R / ZR |
| start | Minus | Plus |
| select | Capture | Home |
| leftStickButton | L-stick | R-stick |
| dpad | — (не дублируем, иначе buttonX и dpad сработают вместе) | — |

Проверка Фазы 1 — Editor, фейковый транспорт с дампом: Kagefumi видит два «Joy-Con 2» как два
`Gamepad`, игроки ходят и топают.

### Фаза A — Windows (Standalone + Editor) ◐ (работает на железе, цифры ещё не записаны)

- ☑ `Native/windows`: C++/WinRT DLL `Joycon2Ble.dll` (CMake + Ninja, статический CRT → не нужен
  VC++ redist). Сборка: `powershell -File Native/windows/build.ps1` (кладёт DLL в
  `Assets/Joycon2/Plugins/Windows/x86_64/`; **Editor держит DLL залоченной — перед пересборкой
  закрыть Unity**). Устройство:
  - passive `BluetoothLEAdvertisementWatcher`, фильтр manufacturer data по company id, троттлинг
    250 мс на адрес; PID и режим SYNC разбирает C#;
  - `FromBluetoothAddressAsync(addr, addressType)` → `GattSession.MaintainConnection = true` →
    `RequestPreferredConnectionParameters(ThroughputOptimized)` (Win11) → `GetGattServicesAsync(Uncached)`
    с ретраями → все характеристики в кэш; лог интервала соединения и MTU;
  - подписка: `ValueChanged` + CCCD Notify; запись: `WriteValueWithResultAsync(WriteWithoutResponse)`;
  - один поток-воркер на устройство (операции строго по порядку), WinRT-колбэки только кладут события
    в очередь, C# забирает их через `jc2_poll_event` — никаких колбэков через границу.
- ☑ Smoke-тест DLL без Unity (консольный probe): скан реальной рекламы (Apple `0x004C`) → события
  доходят, `jc2_shutdown` 4 мс, повторный `jc2_init` работает. Joy-Con'ов в тесте ещё не было.
- ☑ Не спаривать через «Параметры Windows» (Joy-Con рвёт SMP-pairing) — код никогда не вызывает pairing.
- ☑ **Проверка на железе** (2026-10-12, со слов пользователя: «на винде работает хорошо»; Гц/интервал не записаны): Play → зажать SYNC на Joy-Con → `[Joycon2] … ready as gamepad`
  в консоли, оверлей F9 показывает кнопки/стики/Гц; маппинг «боком» сверить с таблицей.
- ☐ Проверка в билде (Mono и IL2CPP) — тот же DLL. Выход из Play Mode / перезагрузка домена
  отключают контроллеры (реализовано, не проверено).
- ☐ Два Joy-Con одновременно, 10 минут без обрывов, частота ≥ 60 Гц (риск: потолок частоты на Windows,
  см. PROTOCOL.md, «Платформенные заметки»).

Почему не C# WinRT напрямую: Unity Editor (Mono) не потребляет WinRT-проекции «из коробки»,
а играть хочется прямо в Editor. Нативный DLL работает везде одинаково.

### Фаза A+ — Гироскоп и механика SUN GRAB ◐ (поза проверена на железе, механику — наиграть)

- ☑ IMU в Input System: `Joycon2GamepadState` = `GamepadState` + `angularVelocity` (°/с) +
  `acceleration` (g) + `attitude` (поза). Оси пересчитаны в систему «боком» (`Joycon2MotionAxes`),
  дрейф гироскопа вычитается (`Joycon2GyroBias`, учится в покое). Оверлей F9 — строки gyro/accel/attitude.
- ☑ Оси на железе (2026-10-13): после исправления (+Y IMU к торцу L/R, +X к правому краю) наклоны
  солнца в тестовом режиме совпали с ожидаемыми. Масштаб гироскопа отдельно (360°) не мерили.
- ☑ Ориентация `Joycon2Orientation`: интеграл гироскопа + коррекция наклона по гравитации; dt — по
  IMU-таймстемпу `0x2A`, если он вменяемый. Ноль (`attitude` = identity):
  - без учёта поворота вокруг вертикали — повернуться корпусом ≠ наклонить, дрейф по горизонтали не мешает;
  - ставится сам через 1,5 с после начала IMU-данных, **по Select** (Capture на L, Home на R), и тихо
    в начале каждого матча Kagefumi;
  - **автоцентр**: ноль за ~20 с подтягивается к позе, в которой Joy-Con держат обычно (только позы
    в пределах 30° от нуля; большие наклоны его не тянут); выключается на время управления солнцем.
- ☑ Kagefumi, **SUN GRAB** (`Assets/Kagefumi/Scripts/SunGrab.cs`): каждые 15 с игры (только днём)
  отстающий по очкам игрок, если он на Joy-Con 2, на 10 с получает солнце. Наклон Joy-Con наклоняет
  солнце от его текущего места на траектории (×1,5, до 80°, не ниже 5° над горизонтом): наклон вправо —
  солнце вправо, тени влево. Ход солнца на это время замирает, потом оно за ~0,6 с возвращается.
  При ничьей, ночью и для не-Joy-Con устройств не срабатывает. Параметры — поля компонента.
  Тестовый режим «солнце из зенита» удалён.
- ☐ Наиграть и подобрать коэффициенты (gain, длительность, интервал, автоцентр).

### Фаза B/C — WebGL, Chrome/Edge на Windows и macOS — B ☑ (Windows работает), C ☑ (macOS: 33 Гц, 66 Гц — с `joycon2boost.app` и одним переподключением)

**Итог по Windows (2026-10-13):** два Joy-Con 2 играют в WebGL-билде в Chrome со всем управлением (кнопки, стики,
гироскоп, SUN GRAB). Сам по себе веб даёт **~17 Гц** (интервал 60 мс — его не изменить из страницы). С
утилитой **`Joycon2Boost.exe`** — **~67 Гц, как в нативном билде**. Утилиту один раз скачать и запустить руками, дальше
игра запускает её сама по B. Другие варианты (дескриптор «Set Report Rate?», экстраполяция позы, подпись/Store/
автозапуск) отложены.

Всё «умное» (handshake, калибровка, маппинг «боком», IMU/поза, LED, keep-alive, SUN GRAB) — то же C#-ядро;
в вебе новый только транспорт байтов. Kagefumi не меняется, кроме способа *подключить* Joy-Con.

- ☑ `Assets/Joycon2/Plugins/WebGL/Joycon2WebBluetooth.jslib` + `Joycon2WebBluetoothTransport.cs`
  (`UNITY_WEBGL && !UNITY_EDITOR`). Та же модель, что у Windows DLL: события в JS-очереди, C# забирает
  `jc2web_poll_event` в `onBeforeUpdate`, колбэков в C# нет. GATT-операции строго по одной на устройство
  (Chrome отвергает параллельные), ретраи `getPrimaryService` сразу после connect, байты записи копируются
  в момент вызова, очередь нотификаций ограничена 2048 (скрытая вкладка не рендерит, но BLE идёт).
- ☑ Интерфейс транспорта: `RequiresUserGesture` + `RequestDevice(json, tag)` → событие `DevicePicked`.
  Нативные транспорты сканируют сами и эти члены игнорируют.
- ☑ `Joycon2Manager.RequestController(side?)`: фильтр `manufacturerData` company `0x0553` + маска на PID
  (`…67 20` L, `…66 20` R — как в рабочем веб-демо), `optionalServices` = сервис `…7fd0`. Сторона — по
  запрошенному фильтру, по имени `Joy-Con 2 (L/R)` или — если Chrome имени не знает («Unknown or
  unsupported device», так было на Windows) — из info-блока Joy-Con (PID по `0x13014`, шаг `ReadInfo` сессии). `CanRequestController`, `ConnectsOnRequest`.
  Адреса в вебе — локальные id страницы (`web#1`), в логах и оверлее.
- ☑ **User gesture.** Kagefumi (лобби/пауза/конец матча): **клик мышью или J** → `RequestController()`
  в том же кадре — это внутри окна transient activation Chrome. Если браузер всё же откажет (нет активации),
  `.jslib` сам ждёт следующего `pointerup`/`keyup` и открывает chooser прямо из DOM-обработчика.
  Кнопки геймпада жестом **не считаются** — подключать Joy-Con кнопкой Joy-Con нельзя.
- ☑ Каждый Joy-Con — отдельный chooser (один `requestDevice` = одно устройство), лимит — `MaxControllers` (2).
- ☑ Детект неподдерживаемого браузера / не-HTTPS: `jc2web_init` → `console.warn`, менеджер выключает Joy-Con.
- ☑ `link.xml` для `Joycon2.Runtime` (layout Input System ищется рефлексией; WebGL стрипает сильнее).
- ☑ Меню **Kagefumi → Build WebGL (dev, localhost)** → `Builds/WebGL` (development, оверлей F9, decompression
  fallback → годится любой статический сервер). Отдать: `python -m http.server 8000 -d Builds/WebGL`,
  открыть `http://localhost:8000` в Chrome/Edge.
- ☑ Модуль Web Build Support установлен; билд собирается и запускается (лобби, оверлей, клик/J → `requestDevice` → ответ браузера обратно в C#) — см. TEST_LOG 2026-10-13. Запрос chooser'а ловится по событию (`onAnyButtonPress`), не `wasPressedThisFrame`: короткий тап иначе теряется.
- ☑ Проверка на железе, Windows 11 + Chrome: два Joy-Con, управление работает, 17 Гц; с `Joycon2Boost` — 15 мс / ~67 Гц
  на обоих (TEST_LOG). Репорты полные (парсер принимает), хотя MTU Chrome не сообщает.
- ☐ Windows: пройти путь целиком после пересборки — первый ручной запуск exe → B в игре → Chrome «Открыть?» → 67 Гц;
  скачивание по второму B; Edge; переподключение после выключения Joy-Con; LED по слоту.
- ☑ **macOS + Chrome (Фаза C), 2026-10-13** (TEST_LOG): два Joy-Con играют, имена в chooser есть, **~33 Гц** без утилиты.
  - ☑ Графика: на Mac Lit-меши не рисовались (URP 17.3 биндит в WebGL битую «пустую» карту теней, ANGLE→Metal отклоняет
    draw) → `Assets/Kagefumi/Scripts/WebGLShadowmapFix.cs` (только WebGL-плеер).
  - ☑ **66 Гц с `joycon2boost.app`** (`Native/macos/tools/joycon2boost.m`, собирается `build.sh`, zip — для скачивания из
    игры). macOS задаёт интервал при *создании* соединения и даёт 15 мс, если в bluetoothd уже есть клиент с приватными
    опциями низкой задержки; присоединение к готовому соединению ничего не меняет. Утилита присоединяется ко всем
    соединениям Chrome с этими опциями → Joy-Con, подключённые после неё, сразу ~66 Гц. Первый остаётся ~33 Гц: утилита его
    отпускает, игра (`RefreshSlowLinks`) закрывает и просит SYNC + повторный выбор (сам Joy-Con без SYNC не возвращается) —
    игрок и gamepad сохраняются. `setDesiredConnectionLatency:0` сбрасывает ускорение — не звать. Chooser Chrome уже
    подключённые устройства не показывает.
  - ☑ Запуск из игры: B → `joycon2boost://start` (та же ссылка, что на Windows; `Joycon2RateBooster` различает ОС),
    вторая B — скачать `Joycon2Boost-mac.zip` (☐ не проверено). Первый запуск руками: правый клик → Open (ad-hoc подпись).
  - ☐ Keep-alive: > 10 мин без обрывов в вебе на Mac.
- ☐ Перезагрузка страницы рвёт соединения; переподключение — снова SYNC + chooser
  (app-level bonding в вебе невозможен: нет адреса хоста).

**Частота репортов — подтвердилось: ~17 Гц.** Chromium на Windows не запрашивает параметры соединения, Joy-Con
сам не просит → дефолт Windows 60 мс. Из страницы не изменить. Но у Windows одно LE-соединение на устройство
для всех процессов: **`Joycon2Boost.exe`** (`Native/windows/tools`, собирается вместе с DLL) просит
`ThroughputOptimized` для уже подключённых браузером Joy-Con → 15 мс, ~67 Гц (проверено, TEST_LOG). Это уже не
«чистый веб» (нужно запустить exe), годится для наших тестов. Запуск из игры:
  - первый ручной запуск exe регистрирует протокол `joycon2boost://` в HKCU (без админа; `--unregister` — убрать);
  - игра (`Joycon2RateBooster`) видит, что Joy-Con < 30 Гц, и пишет подсказку; **B** → `joycon2boost://start`
    (Chrome один раз спрашивает «Открыть…?», можно «всегда разрешать»). Связи с утилитой нет — успех виден по Гц;
  - если через 6 с частота не выросла — **B ещё раз** скачивает `Joycon2Boost.exe` (лежит рядом с index.html,
    кладёт `Kagefumi → Build WebGL`); неподписанный → SmartScreen «Подробнее → Выполнить в любом случае»;
  - запущенная браузером копия одна (mutex) и сама закрывается через 60 с без подключённых Joy-Con. Без него — 17 Гц: стик и кнопки играбельны, поза не
«плывёт» (dt по IMU-таймстемпу), солнце в SUN GRAB дёргается → вариант на будущее: экстраполяция позы по гироскопу.

### Фаза D — macOS (Standalone + Editor) ◐ (два Joy-Con работают в Editor с высокой частотой; билд с Joy-Con — проверить)

Готового решения нет (проверено 2026-10-13): Apple GameController и Unity Input System Joy-Con 2 не видят, SDL3 —
только USB (BLE-форк — только Windows), switch2mac — виртуальный HID (без лицензии, entitlement от Apple, в Unity
был бы `Joystick`), JoyCon2Mac — DriverKit с выключенным SIP и L+R в один пад. Поэтому — свой тонкий транспорт.

- ☑ `Native/macos/src/joycon2ble.m`: Obj-C (ARC) `.bundle`, universal arm64 + x86_64, macOS 11+, **тот же C ABI**, что у
  Windows DLL (`Native/windows/include/joycon2ble.h` стал общим). Сборка: `Native/macos/build.sh` (только Xcode CLT) →
  `Assets/Joycon2/Plugins/macOS/Joycon2Ble.bundle` (ad-hoc подпись). **Editor не выгружает bundle: после Play Mode
  перед пересборкой закрыть Unity.** Устройство:
  - один последовательный `dispatch_queue` владеет `CBCentralManager` и всеми `CBPeripheral` → состояние без локов;
    колбэки только кладут события в очередь, C# забирает `jc2_poll_event` — как на Windows;
  - скан `scanForPeripheralsWithServices:nil` + `AllowDuplicates` (иначе вход в SYNC можно не увидеть), фильтр по
    company id в manufacturer data, троттлинг 250 мс на устройство;
  - CoreBluetooth **не отдаёт MAC**: «адрес» — локальный id процесса по `peripheral.identifier` (в логах `mac#1`;
    `IJoycon2Transport.LocalAddressPrefix`);
  - connect → `discoverServices:nil` → все характеристики → `CONNECTED` (MTU = `maximumWriteValueLength` + 3);
    операции до этого момента откладываются; write-without-response — через очередь и
    `peripheralIsReadyToSendWriteWithoutResponse` (иначе CoreBluetooth молча теряет записи при полном буфере);
  - выключение Bluetooth → `SCAN_STOPPED` + `DISCONNECTED` всем (CoreBluetooth в этом случае `didDisconnect` не шлёт).
- ☑ C#: `Joycon2WindowsTransport` → **`Joycon2NativeTransport`** (Win + Mac, `UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX`).
- ☑ **TCC.** Без `NSBluetoothAlwaysUsageDescription` в Info.plist macOS убивает процесс на первом вызове CoreBluetooth
  (проверено: SIGABRT `__TCC_CRASHING_DUE_TO_PRIVACY_VIOLATION__`). У Unity Editor 6000.3.18f1 ключ есть. Для билда —
  Player Settings → macOS → **Bluetooth Usage Description** (заполнено; `Kagefumi → Build macOS (dev)` заполняет, если
  пусто). `jc2_init` без ключа возвращает `-2` вместо падения, C# пишет, что делать. Отказ в доступе →
  лог «System Settings → Privacy & Security → Bluetooth».
- ☑ Keep-alive 1 Гц — уже в C#-ядре (LED), на Mac ничего добавлять не нужно.
- ☑ Smoke-тест без Unity: `Native/macos/build/joycon2probe.app` (запуск через `open`, см. TEST_LOG), режим `--joycon` —
  минимальный handshake и Гц без Unity.
- ☑ Editor (Play) и IL2CPP-билд `Kagefumi → Build macOS (dev)` → `Builds/macOS/Kagefumi.app`: транспорт стартует, Bluetooth
  powered on, скан идёт.
- ☐ **Проверка на железе:** Play → SYNC на Joy-Con → `[Joycon2] … ready as gamepad`; оба Joy-Con; Гц в F9 (ожидаем
  ~66); связь не рвётся > 1 мин (keep-alive); маппинг/гироскоп как на Windows; повторное подключение после выключения.
- ☐ Подпись/notarization для раздачи не делалась; Gatekeeper на чужом Mac — `xattr -dr com.apple.quarantine Kagefumi.app`.
- ☑ **Частота.** Первый прогон на железе: два Joy-Con работают, но **~30 Гц** — дефолтный интервал macOS 30 мс,
  публичного API нет. Плагин теперь подключается с приватными опциями (`RequiresLowLatency`, `LatencyCritical`,
  `OverrideMin/MaxCIFrames = 6`, как JamCon → 15 мс / ~66 Гц) и логирует фактический интервал
  (`connection interval … ms` — при первом подключении этот делегат не приходит). **Проверено: работает** (пользователь,
  2026-10-13; handshake 350 мс вместо 1,7 с). Эксперименты без пересборки: `JC2_MAC_CI_FRAMES` (0 = выкл., меньше 6 —
  короче?), `JC2_MAC_DESIRED_LATENCY=0` (SPI `setDesiredConnectionLatency`). Если не поможет — запись `85 00` в
  дескриптор `679d5510…` (нужен новый вызов транспорта `write descriptor`; PROTOCOL.md).

### Фаза E — WebGL на Android (Chrome) ☐

- ☐ Тот же `.jslib`. Проверить разрешения «Устройства поблизости»/геолокацию, производительность
  WebGL-билда на телефоне, MTU.

### Фаза F — Android нативно ◐ (APK работает на Pixel 3 / Android 12; Гц и длительный прогон — записать)

- ☑ Исследование 2026-10-13: публичные Android-реализации, чужие тесты двух Joy-Con на ~67–68 Гц,
  переиспользование нашего C#-ядра, Android API и путь к Development APK — [ANDROID_RESEARCH.md](ANDROID_RESEARCH.md).
  Для gamepad внутри Unity Shizuku/root не нужны. Последующий аппаратный результат — TEST_LOG.
- ☑ AAR: `Native/android/src/com/vivoderin/joycon2/BleTransport.java` (собственный Java, без зависимостей),
  сборка `Native/android/build.ps1` через Unity OpenJDK/SDK → `Assets/Joycon2/Plugins/Android/joycon2ble.aar`.
  `BluetoothLeScanner` с `ScanFilter.setManufacturerData(0x0553, …)` → `connectGatt(…,
  TRANSPORT_LE)` → `requestMtu(517)` и проверка согласованного MTU (не обязан быть 517;
  для полного ответа нашего factory-read нужен минимум 92) → discovery → `Connected` →
  `setCharacteristicNotification` + запись CCCD-дескриптора → очередь.
- ☑ Разрешения в manifest AAR: `BLUETOOTH_SCAN` (`neverForLocation`) + `BLUETOOTH_CONNECT` на API 31+,
  legacy Bluetooth permissions и `ACCESS_FINE_LOCATION` на API ≤ 30. Runtime-запрос через
  `UnityEngine.Android.Permission`; запуск менеджера после разрешения и повторный запуск после отказа.
- ☑ `Joycon2AndroidLifecycle`: запрос разрешений до запуска менеджера, UI отказа/настроек и повторного запроса.
- ☑ Строго последовательные GATT-операции на handle; один HandlerThread. Timeout закрывает GATT,
  чтобы поздний callback не завершил следующую запись; callbacks старых GATT отсекаются.
- ☑ `Joycon2AndroidTransport` + ветка `UNITY_ANDROID` в `CreateTransport`, polling без Unity API в Java callbacks.
  JNI event frame 32 байта LE + payload, signed bytes сохраняются; 6 тестов границы JNI, всего Joycon2 43/43 passed.
- ☑ Публичный `CONNECTION_PRIORITY_HIGH` на обоих соединениях, повторный запрос после подключения второго.
- ☑ Пауза закрывает BLE/устройства, возврат снова запускает скан (нужен SYNC); Bluetooth off обрабатывается в Java;
  touch-кнопки Connect/retry, Bluetooth/App settings, Details, Record. Реализация готова, ☐ проверить на телефоне.
- ☑ Меню `Kagefumi → Build Android APK (Joy-Con dev)`: ARM64 IL2CPP Development, landscape,
  `Builds/Android/Kagefumi-Joycon2-dev.apk`, package `com.vivoderin.kagefumi`.
- ☑ APK собран: 51,8 MiB, ARM64 IL2CPP Development, min SDK 25 / target 36, 0 ошибок.
  `Native/android/verify-apk.ps1`: подпись v2, объединённый manifest с BLE-разрешениями,
  класс транспорта в DEX и native player libraries проверены; результаты — TEST_LOG.
- ☑ Проверка APK на Pixel 3 / Android 12 (2026-10-13, пользователь: «отлично работает»).
  Общая работоспособность подтверждена; отдельные измерения Гц/MTU и сценарии стабильности не сообщены.
- ☐ Development APK: два отдельных игрока, gyro/SUN GRAB, ≥ 60 Гц на каждый, 10 минут, переподключение через SYNC.
- ☑ Текущий `AndroidMinSdkVersion: 25` — ок; AndroidPlayer/OpenJDK/SDK/NDK установлены.

### Фаза G — WebGL на iOS/iPadOS через Bluefy ☐

- ☐ Safari не поддерживает Web Bluetooth → только браузер **Bluefy** (WKWebView с Web BLE).
- ☐ Bluefy не понимает фильтр `manufacturerData` → фильтр по имени `Joy-Con 2 (L)` / `Joy-Con 2 (R)`
  (ветка в `.jslib` по `navigator.userAgent.includes("Bluefy")`).
- ☐ Проверить, тянет ли WKWebView наш Unity WebGL-билд по памяти (особенно iPhone). При необходимости —
  облегчённый билд (сжатие текстур, без пост-процесса).
- ☐ В Safari показывать ссылку `bluefy://open?url=…`.

---

## 5. Критерии успеха (на каждой платформе)

1. Два Joy-Con 2 подключены **одновременно**, каждый — отдельное устройство `Gamepad` в Input System.
2. В Kagefumi оба заходят в лобби как P1 и P2 нажатием кнопки и играют матч до конца.
3. На Joy-Con горят индикаторы игрока, соответствующие слоту (P1 / P2).
4. Частота репортов ≥ 60 Гц на контроллер, без заметной задержки.
5. 10 минут игры без обрывов; при выключении Joy-Con слот освобождается, повторное подключение
   работает без перезапуска игры.
6. Результат записан в `Docs/Joycon2/TEST_LOG.md` (ОС, браузер, адаптер, прошивка, Гц, проблемы).

---

## 6. Риски и открытые вопросы

| Риск | Чем грозит | Что делаем |
|---|---|---|
| Протокол — результат реверса сообщества; обновление прошивки Joy-Con может что-то сломать | Перестанет подключаться | Фиксируем версию прошивки в TEST_LOG; парсер с проверками длины, логирование сырых байт |
| Слабый встроенный BT-адаптер / Windows 10 | Обрывы, низкий Гц, не держит 2 устройства | Тестируем на Win 11; держим под рукой USB BT 5.x донгл |
| Windows: контроллер не просит короткий connection interval | Win10 ≈ 16,7 Гц; на Win11 зависит от того, примет ли стек `ThroughputOptimized` | Просим `ThroughputOptimized`, логируем фактический интервал; если < 60 Гц — искать обход (повторный запрос после handshake, донгл) |
| Web: `requestDevice` требует жеста пользователя | Chooser не открывается | Вызов в кадре клика/клавиши; иначе `.jslib` ждёт следующий DOM-жест |
| Web (Windows): Chrome не управляет connection interval | 17 Гц (подтверждено) → дёрганый SUN GRAB | `Joycon2Boost.exe` → 67 Гц; без него — экстраполяция позы в игре |
| Web: в каком сервисе vibration-характеристики | Нет доступа к вибрации без `optionalServices` | Выясняем в Фазе 0; вибрация — не обязательна для MVP |
| Android: MTU 23 по умолчанию | Обрезанные репорты | `requestMtu(517)` до подписки |
| macOS: тихий обрыв ~15 c | Отваливается контроллер | Keep-alive 1 Гц в общем ядре |
| macOS Editor: TCC-разрешение Bluetooth | Ничего не находит в Editor | Инструкция; проверка `CBManagerAuthorization` и вывод в лог |
| iOS: только Bluefy, лимиты памяти WKWebView | Билд не грузится | Облегчённый WebGL-билд; это P4 |
| Переподключение без SYNC (app-level bonding, команда `0x15`) | Каждый запуск — жать SYNC | Не нужно для MVP; изучить после Фазы D. В вебе невозможно |
| Лицензии референсных репо (часть без лицензии) | Нельзя копировать код | Пишем свою реализацию по описанию протокола; код берём только из MIT-проектов с атрибуцией |
| Режим «пара L+R = один геймпад» | — | Вне скоупа, легко добавить в C#-слое |

---

## 7. Чем НЕ занимаемся (пока)

- Оптическая «мышь» Joy-Con 2, магнитометр — не парсим. (Гироскоп/акселерометр теперь выведены в
  Input System — см. Фазу A+.)
- HD-вибрация (сложный формат пакетов) — максимум простой пресет после MVP.
- NFC, ИК-камера, аудио, GameCube/Pro Controller 2 (хотя протокол общий — бонусом может заработать).
- Нативный iOS.

---

## 8. Запасной план (если свой транспорт где-то не взлетит)

На десктопе есть сторонние драйверы, которые превращают Joy-Con 2 в стандартный геймпад для ОС —
тогда Unity увидит их «как есть», без нашего кода (но игроку придётся ставить софт):

- Windows: Joycon2PC (ViGEmBus → виртуальный Xbox 360), Switch2Connect, joycon2-connector.
- macOS: switch2mac (виртуальные game controllers), JoyCon2Mac (DriverKit).
- Android: joycon2android (виртуальные HID через Shizuku).

Полезно в Фазе 0 как эталон «железо и адаптер в порядке». Для веба запасного плана нет.

---

## 9. Источники

- Протокол (самый полный практический референс, проверено на железе, в т.ч. macOS keep-alive):
  <https://github.com/loserkidsblink/switch2mac/blob/main/research/PROTOCOL.md>
- Исходный реверс: <https://github.com/ndeadly/switch2_controller_research>
- Web Bluetooth демо (MIT; Chrome/Edge + Bluefy): <https://github.com/mascii/joy-con-2-web-bluetooth-api-demo>,
  живое демо <https://joy-con-2-web-bluetooth-api-demo.pages.dev/>
- Windows: <https://github.com/FingerlessCoder/joycon2pc> (MIT, C#/ViGEm),
  <https://github.com/TheFrano/joycon2cpp> (C++/WinRT), <https://github.com/Nohzockt/Switch2-Controllers> (Python/bleak),
  <https://github.com/TommyWabg/Switch2Connect>, <https://github.com/Logan-Gaillard/Joy2Win>
- macOS: <https://github.com/loserkidsblink/switch2mac>, <https://github.com/seitanmen/Joycon2forMac>,
  <https://github.com/OZORDI/JoyCon2Mac>
- Android: <https://github.com/JoeGeC/joycon2android> (GPL-3.0; upstream для SWeav02),
  <https://github.com/patrickmerkel26-dev/Joycon-2-Android-Bridge> (MIT, Java, чужой dual-controller тест),
  [исследование для нашего APK](ANDROID_RESEARCH.md).
- SDL: в 3.4 есть HIDAPI-драйвер Switch 2 (USB, Pro Controller 2), BLE Joy-Con 2 — только в форках
  (<https://github.com/hifihedgehog/SDL/issues/38>); для нас не вариант.
