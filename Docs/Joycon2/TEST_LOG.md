# Joy-Con 2 — журнал проверок

Формат: дата · платформа/ОС · адаптер · что проверяли · результат. Для проверок с Joy-Con —
версия прошивки (если известна), частота (Гц из оверлея F9), обрывы, ссылки на дампы.

## 2026-10-14 · iPhone 13 mini / iOS 18.7.7 · нативный iOS (CoreBluetooth): два Joy-Con 2, 60+ Гц

Сборка: `Native/ios/build.sh` (тот же `Native/macos/src/joycon2ble.m` → `libJoycon2Ble.a`, arm64) →
`Kagefumi → Build iOS Xcode project (Joy-Con dev)` (IL2CPP Development, 106 с первый раз, 44 с повторно, 0 ошибок) →
`Native/ios/deploy.sh` (автоподпись, team `59P2VDSY5D`, `com.vivoderin.kagefumi`, установка по USB через `devicectl`).
Первая установка падала на монтировании Developer Disk Image, пока телефон был заблокирован
(`kAMDMobileImageMounterDeviceLocked`); после разблокировки всё поставилось.

Лог приложения (`devicectl … --console`):

| Шаг | Результат |
|---|---|
| Bluetooth | диалог доступа iOS → разрешено; `Bluetooth powered on`, скан company `0x0553` |
| Joy-Con 2 (R) `ios#1` | RSSI −40, link up 280 мс, готов за 918 мс, **MTU 512**, factory-калибровка, gamepad, **LED P1** |
| Joy-Con 2 (L) `ios#2` | RSSI −47, link up 263 мс, готов за 893 мс, **MTU 512**, factory-калибровка, gamepad, **LED P2** |
| Имя в рекламе | `(null)`: сторону берём из PID в manufacturer data, как на Mac |
| Частота | пользователь: **«все работает, частота 60+ отлично»** (Details). Подключение шло с приватными опциями низкой задержки (`CI frames 6`), как на Mac. Без них дефолт iOS 30 мс ≈ 33 Гц, значит опции действуют и на iOS. Приватный делегат интервала при первом подключении не пришёл (так же, как на Mac), поэтому интервал в мс не записан |

Первый запуск: диалог разрешения Bluetooth вызывает `OnApplicationPause`. Первая версия на этом
перезапускала менеджер. Исправлено: перезапуск только после паузы > 5 с (фон), короткие паузы (диалог, шторка)
связь сохраняют. Проверено пользователем в общем «все работает»; отдельные результаты шторки, фона/возврата,
10-минутного прогона, гироскопа/SUN GRAB не сообщались.

## 2026-10-14 · Pixel 3 / Android 12 / Chrome · Android Boost v0.2: оба 60+ Гц

Первый helper v0.1 не изменил частоту (пользователь: **20–22 Гц**). Служба была foreground,
BLUETOOTH_CONNECT выдан, но Bluetooth dump показывал только GATT-клиенты Chrome.
Причина подтверждена v0.2: `getConnectedDevices(GATT)` отдаёт оба Joy-Con **без имени**,
поэтому строгий фильтр имён v0.1 их пропускал.

v0.2 установлен, адреса взяты из GATT dump телефона и переданы как конфигурация helper,
а не зашиты в исходники. Подключён к обоим: L `98:E2:55:01:93:0E`, R `98:E2:55:00:DB:B5`.
Оба HIGH accepted=true. В системном logcat `BluetoothGatt.onConnectionUpdated` для
процессов helper и Chrome: **interval=12 (15 мс), latency=0, timeout=500, status=0**
на обоих (00:32:27–00:32:48). Это аппаратное подтверждение изменения общих соединений.

Пользователь после v0.2: **«теперь 60+ все супер»**. Ускорение обоих до 60+ в нашей
Android WebGL-игре подтверждено; точные Гц, firmware и 10-минутный прогон не сообщены.

По просьбе пользователя реализовано упрощение v0.3: Boost в странице → helper
автоматически стартует и возвращает Chrome. UUID выбранного сервиса передаётся из
C#-фильтров; неизвестные/безымянные устройства проверяются через GATT discovery
перед HIGH, без Nintendo-команд/парсера в Java. Короткий helper-hop сохраняет ссылки.
10 Node-тестов прошли; APK v0.3 с v2/v3-подписью обновлён на Pixel 3, deep link
resolve-activity → BoostActivity подтверждён. Финальный WebGL build: 65 с, 112 MiB,
0 ошибок; APK включён в веб-сборку и архив.

v0.3 проверен на том же Pixel 3: helper log сообщает `target service verified`
для обоих устройств (R около 00:46:39, L около 00:47:10), затем HIGH accepted.
На вопрос о Boost → автоматическом запуске → возврате в Chrome → Connect L/R,
сохранении 60+ Гц и необходимости ручного запуска пользователь ответил:
**«все отработало нормально»**, затем разрешил коммит и пуш Android-веб-логики.
Полный новый путь и определение устройств по сервису подтверждены. Отдельный
прогон SUN GRAB, точные Гц, firmware и 10-минутная стабильность остаются открытыми.

## 2026-10-14 · Pixel 3 / Android 12 / Chrome · два Joy-Con в WebGL: ~20 Гц

Пользователь: «не мега удобно, но норм и в целом работает»; частота обоих Joy-Con
около **20 Гц**. На уточнение Game FPS ответил **«Около 50–60 FPS или выше»**.
Работа двух Joy-Con в телефонном браузере подтверждена пользователем. Отдельные
результаты для гироскопа/SUN GRAB, длин notifications, LED и длительной стабильности
не сообщались. BLE-интервал не измерен; ~50 мс — гипотеза по частоте и AOSP BALANCED.

Для ускорения подготовлен `Native/android/booster`: отдельный foreground-service
APK запрашивает публичный HIGH на общем физическом соединении. Сборка OpenJDK/SDK 36:
Java, DEX, manifest, zipalign, подпись v2/v3 прошли. `Joycon2Boost-test.apk` — 16 727 байт;
ADB установка завершилась Success (обычный streamed install после отказа incremental).
Activity открыта и подтверждена как resumed на телефоне. Пока это проверка сборки,
установки и запуска UI — **HIGH и прирост частоты ещё не подтверждены**.

Следующий шаг: Start boost → Nearby devices → вернуться в Chrome → заново SYNC/Connect
L/R (уход в helper закрывает браузерные ссылки) → измерить оба Гц, lengths/gyro/FPS.
Контроллеры и телефон проверяются в игре; статус accepted у helper не считается измерением.

## 2026-10-14 · Pixel 3 / Android 12 / Chrome 154.0.8037.92 · подготовка веб-теста по USB

Телефон виден как авторизованное ADB-устройство. Версии Android и установленного Chrome
прочитаны с телефона. Локальный сервер ПК на порту 8765 доступен; настроен и проверен
`adb reverse tcp:8765 tcp:8765`. Адрес для Chrome на телефоне: `http://localhost:8765/`.

Автоматическое открытие Chrome было отклонено проверкой разрешений; страницу нужно
открыть вручную. Загрузка Unity на телефоне, подключение Joy-Con, Гц/IMU и стабильность
на этом шаге **не проверены**.

## 2026-10-14 · Android WebGL: реализация и браузерный smoke-тест, без телефона / Joy-Con

Подготовлена мобильная веб-версия: DOM Connect L/R с прямым вызовом chooser из жеста,
экранная диагностика L/R и FPS, Record/Stop/Download TSV, HTTPS/browser/policy/permission
сообщения, контролируемое отключение при уходе страницы в фон (кроме открытого chooser).
Ядро Nintendo-протокола и маппинг не менялись.

| Проверка | Результат |
|---|---|
| 8 Node-тестов `Native/web/tests/web-controls.test.cjs` | ☑ прямой жест, отдельные L/R и один chooser, отмена/отказ/retry, fallback на свежий жест, HTTPS/browser/iframe policy, старый результат chooser после shutdown, фон/поздний connect, recording actions и Blob cleanup |
| Unity 6000.3.18f1, Development WebGL в отдельной рабочей копии (открытый Editor не закрывали) | ☑ первый build 540 с после импорта; окончательный build с последними C#-правками 75 с, 112 MiB, 0 ошибок; логи `Logs/Joycon2AndroidWebBuild*.log` |
| Сборка и архив | ☑ `Builds/WebGL`, `Builds/Kagefumi-WebGL-Android-test.zip` — 30 414 848 байт (~29 MiB) |
| Встроенный браузер Codex (Chromium), `http://127.0.0.1:8765` | ☑ лобби/графика, UI и счётчик FPS работают, ошибок JS/WASM в проверенном логе нет |
| Connect L в браузере | ☑ вызов принят, кнопки выключены на время chooser; аппаратный выбор устройства не выполнялся. Отмена/отказ и раздельные L/R проверены фейковым Web Bluetooth в Node |
| Details → Record → Stop → Download recording | ☑ файл TSV скачан в Downloads; без контроллеров содержит только заголовок `# time\tside\treport-hex`, файл теста удалён после проверки |
| Размеры экрана 844×390 и 390×844 | ☑ кнопки доступны; портретный экран показывает поворот телефона; ширина панели в портрете 374 px, границы x=8…382 при ширине 390 px. Снимок: `Builds/Joycon2AndroidWebPreview.jpg`. Это размеры desktop Chromium, не Android-эмуляция |
| Телефон по USB | Не подключён (`adb devices` пуст); Android Chrome, два Joy-Con, Гц/MTU/IMU и стабильность **не проверялись** |

**Следующий аппаратный прогон:** Pixel 3 / Android 12 + актуальный Chrome по HTTPS или
телефонному localhost через `adb reverse` (см. `Native/web/README.md`). Connect L/R →
два игрока, длина notifications и gyro/SUN GRAB, Гц обоих, FPS, 10 минут, фон/возврат,
отказ доступа и переподключение. Частота ≥ 60 Гц остаётся открытой.

## 2026-10-12 · Windows 11 Home 10.0.26300 · Qualcomm FastConnect 7800 (BT 5.3)

Без Joy-Con: их в этой проверке не было.

| Что | Результат |
|---|---|
| Сборка `Native/windows` (VS Build Tools 2022, MSVC 14.44, SDK 10.0.26100, CMake+Ninja) | ☑ без предупреждений `/W4`; экспорт 10 функций `jc2_*`; зависимости — только системные API-set'ы (VC++ redist не нужен) |
| `Joycon2Ble.dll` из консольного probe (.NET 10, P/Invoke): init → scan company `0x004C` 6 с | ☑ 34 события `DEVICE_FOUND` от соседних Apple-устройств, manufacturer data и RSSI корректны, троттлинг работает |
| Скан company `0x0553` (Nintendo) 4 с | ☑ 0 событий (Joy-Con рядом не было / не в SYNC) — ожидаемо |
| `jc2_shutdown` → `jc2_init` → `jc2_shutdown` | ☑ shutdown 4 мс, повторный init успешен |
| Ядро C#: 22 теста (`dotnet test`, вне Unity) | ☑ все прошли |
| Runtime-сборка компилируется Roslyn из Unity 6000.3.18f1 (Editor Win / Standalone Win / прочие платформы) | ☑ без ошибок и предупреждений |

**Дальше:** подключить Joy-Con 2 в Editor (Play → SYNC), записать сюда фактические интервал
соединения/MTU из лога, Гц, результат маппинга «боком», затем — билд.

## 2026-10-12 · Windows 11 · Joy-Con 2 — первая проверка на железе

Со слов пользователя: подключение и игра в Kagefumi на Windows «работают хорошо». Фактические
интервал соединения, MTU, Гц, Editor/билд и Mono/IL2CPP не записаны (в ProjectSettings для Standalone
выставлен IL2CPP) — дописать при следующем прогоне.

Гироскоп/SUN GRAB на железе ещё не проверялись (код добавлен после этого прогона).

## 2026-10-13 · Windows 11 · гироскоп, тестовый режим солнца

Наблюдение пользователя (один Joy-Con, сторона не записана): наклон вправо → тень вниз экрана,
наклон вверх → тень вправо, то есть всё повёрнуто на 90° вокруг вертикали. Камера смотрит вдоль +Z
(проверено по `KagefumiSceneBuilder`), значит ошибка в предположении об осях IMU: на самом деле
+Y — к торцу L/R, +X — к правому краю. Таблица `Joycon2MotionAxes` исправлена для обеих сторон
(вторая — по симметрии), направления солнца теперь берутся относительно камеры. Ждёт повторной проверки на обоих Joy-Con.

## 2026-10-13 · Windows 11 · оси после исправления

Пользователь: в тестовом режиме солнца «всё гуд работает» — наклоны совпали с ожидаемыми.
Тестовый режим после этого удалён, гироскоп снова управляет солнцем только в SUN GRAB.

## 2026-10-13 · Windows 11 · WebGL / Web Bluetooth — без железа и без браузера

Модуля Web Build Support для 6000.3.18f1 нет → WebGL-билд пока не собирался.

| Что | Результат |
|---|---|
| `Joycon2.Runtime` + `Assembly-CSharp` компилируются Roslyn из Unity с дефайнами WebGL-плеера, Windows-плеера и Editor | ☑ без ошибок; в WebGL-сборку попадает только `jc2web_*`, в Windows — только `jc2_*` |
| `Joycon2WebBluetooth.jslib` в node против фейкового `navigator.bluetooth` | ☑ chooser (и без активации → ждёт клик), id устройства стабилен при повторном выборе, connect → subscribe → write строго по одной операции, копия буфера записи, нотификации с тегом, лимит очереди 2048, обрыв → `Disconnected`, свой disconnect — без события, отмена chooser → статус 1 |

**Дальше:** поставить модуль, `Kagefumi → Build WebGL`, `python -m http.server 8000 -d Builds/WebGL`,
Chrome → `http://localhost:8000`, SYNC → клик → выбрать Joy-Con; записать Гц, MTU-проблемы, Chrome vs Edge.

## 2026-10-13 · Windows 11 · первый WebGL-билд, без Joy-Con

Модуль Web Build Support установлен. `Kagefumi → Build WebGL (dev, localhost)` из Editor: ☑ успешно,
112 МБ (development, без сжатия), ~6 мин первая сборка, ~1 мин повторная. Отдаётся `python -m http.server`
с `localhost:8000`. Проверял во встроенном браузере Claude (Chromium 152) — **не Chrome, без Joy-Con**.

| Что | Результат |
|---|---|
| Загрузка, URP/WebGL 2, лобби, HUD, оверлей F9 | ☑ всё рисуется; в консоли только стандартные «shader not supported» для отладочных URP-шейдеров |
| `[Joycon2] started (Web Bluetooth)` | ☑ транспорт поднялся (`navigator.bluetooth` есть, secure context) |
| Клавиатурный игрок (SPACE → P1 READY) | ☑ |
| Клик по игре → `RequestController` → `requestDevice` | ☑ тост «PICK YOUR JOY-CON 2», статус «Bluetooth chooser open»; браузер принял фильтры (нет TypeError) и вернул `NotFoundError: User cancelled` → C# пишет «no Joy-Con picked», статус сбрасывается, можно снова |
| J → то же | ☑ (после исправления ниже) |
| **Баг, исправлен:** короткий клик/тап (down+up в одном кадре) не открывал chooser — `wasPressedThisFrame` его не видит | Теперь запрос ловится по событию в `onAnyButtonPress` (как вход по SPACE) |

Окна выбора устройства встроенный браузер не показывает, поэтому подключение Joy-Con, Гц, MTU, гироскоп
и SUN GRAB в вебе ещё **не проверены** — нужен обычный Chrome/Edge и железо.

## 2026-10-13 · Windows 11 · Chrome · Joy-Con 2 — первый заход в вебе

Пользователь: chooser показал устройство как **«Unknown or unsupported device (98:E2:55:01:93:0E) — Paired»**,
после «Pair» ничего не происходило. Причина: Chrome не знал имени, а сторона L/R бралась только из имени
`Joy-Con 2 (L/R)` → менеджер писал предупреждение и не подключался. Исправлено: без имени сессия первым
шагом читает info-блок `0x13000` и берёт PID по `+0x14` (ndeadly, memory_layout.md). «Paired» — у Windows
есть запись о спаривании этого Joy-Con; если подключение будет рваться — удалить его в «Bluetooth и устройства».
Ждёт пересборки и повторной проверки.

## 2026-10-13 · Windows 11 · Chrome · два Joy-Con 2 в вебе, частота

Пользователь: после исправления L/R оба Joy-Con подключаются и играют в WebGL-билде, но **оба ~17 Гц**
(в Editor через `Joycon2Ble.dll` — интервал 15 мс, ~66 Гц, по логу `[Joycon2/native] connection interval 15.00 ms`).

- Причина: Chromium (`device/bluetooth/bluetooth_device_winrt.cc`) параметры соединения не запрашивает, Joy-Con
  сам Connection Parameter Update не шлёт (ndeadly) → дефолт `bthport.sys` 60 мс. Из страницы не изменить.
- `Native/windows/tools/joycon2boost.cpp` → `Joycon2Boost.exe`: открывает тот же Joy-Con (GATT не трогает) и просит
  `ThroughputOptimized`. Прогон при живом подключении Chrome: ☑ **L 98:E2:55:01:93:0E и R 98:E2:55:00:DB:B5:
  60 мс → 15 мс (~67 Гц) за ~1 с**, link timeout 9600 → 2000 мс. Без аргументов находит уже подключённые
  (Windows знает их: «Paired»).
- После закрытия утилиты Windows возвращает 60 мс. Быстрый перезапуск поймал гонку (освобождение старого запроса
  пришло после нового → снова 60 мс) — утилита теперь перепроверяет через 2 с и повторяет запрос (до 5 раз подряд).
- ☐ Подтвердить Гц в оверлее F9 браузера при работающей утилите; ощущение отклика.

## 2026-10-13 · Windows 11 · запуск Joycon2Boost из браузера (без пересборки игры)

- `Joycon2Boost.exe joycon2boost://start` (как его запускает браузер): ☑ без записи в реестр, нашёл оба подключённых
  Joy-Con и поднял до 15 мс; второй запуск → «already running», код 0.
- Регистрация протокола (ручной запуск) и путь через игру (B → Chrome «Открыть Joycon2Boost?») — ☐ ждут
  пользователя: регистрацию не делал без спроса, игре нужна пересборка.

## 2026-10-13 · Итог дня: WebGL на Windows

Управление Kagefumi целиком (кнопки, стики, гироскоп, SUN GRAB) работает с двумя Joy-Con 2 в WebGL-билде в Chrome на
Windows 11. Частота: **~17 Гц без утилиты**, **~67 Гц с `Joycon2Boost.exe`** (как в нативном билде). Цена 67 Гц —
один раз скачать и запустить exe, дальше игра стартует его сама (B → `joycon2boost://`). Другие пути к частоте
(дескриптор `679d5510…`, экстраполяция позы, подпись/Store/автозапуск) отложены. Следующий шаг — macOS.

## 2026-10-13 · macOS 15.7.4 (arm64) · встроенный Broadcom BCM4388 (BT 5.3) · нативный транспорт, без Joy-Con

Готового решения для Mac нет (PLAN, Фаза D) → свой CoreBluetooth `.bundle` с тем же C ABI, что у Windows DLL.

| Что | Результат |
|---|---|
| Сборка `Native/macos/build.sh` (Xcode 26.3, clang 17, `-Wall -Wextra -Werror`) | ☑ без предупреждений; universal x86_64 + arm64; экспорт 10 функций `jc2_*` |
| probe из терминала Claude Code | ✖ SIGABRT TCC: ответственный процесс — CLI без `NSBluetoothAlwaysUsageDescription` → probe сделан `.app`, запуск через `open` |
| `open -W -n --stdout … joycon2probe.app --args 20 004C` (скан Apple) | ☑ init 12 мс, «Bluetooth powered on», 166 объявлений / 29 устройств за 20 с, троттлинг работает |
| Скан `0x0553` 5 с | ☑ 0 событий (Joy-Con в SYNC рядом не было) — ожидаемо |
| `jc2_shutdown` → `jc2_init` → `jc2_shutdown` | ☑ shutdown 0,2 мс, повторный init успешен |
| Unity Editor 6000.3.18f1, Play | ☑ `[Joycon2] started (CoreBluetooth)` → `[Joycon2/native] Bluetooth powered on` → `scan started (company 0x0553)` |
| `Kagefumi → Build macOS (dev)` (IL2CPP, universal) | ☑ `NSBluetoothAlwaysUsageDescription` в Info.plist, `Contents/PlugIns/Joycon2Ble.bundle`; запуск → те же три строки в Player.log |
| **Баг, исправлен:** оверлей F9 создавал шрифт на каждый `OnGUI`, а на Mac (нет Consolas) ещё и сыпал предупреждение каждый кадр (~65 тыс. строк в логе за минуту) | Шрифт создаётся один раз, из установленных |

**Дальше (нужны Joy-Con):** Play → SYNC → `ready as gamepad`; оба Joy-Con; Гц в F9; > 1 мин без обрыва; маппинг,
гироскоп, SUN GRAB; то же в билде. Без Unity: `Native/macos/build/joycon2probe.app` с `--args --joycon 30`.

## 2026-10-13 · macOS 15.7.4 · два Joy-Con 2 в Editor — частота

Пользователь: оба Joy-Con подключаются и работают в Editor (L/R, LED P1/P2, калибровка factory), но **~30 Гц**.
Причина — дефолтный интервал соединения macOS 30 мс (Joy-Con не просит короче, публичного API нет; switch2mac видел
те же 30 мс). GATT на macOS: сервис `…7fd0` целиком (в т.ч. `4147423d…`, `d3bd69d2…`, `…7fde/7fdf`) + сервис `00c5af5d…`.

Исправление (ждёт проверки, Unity нужно перезапустить — старый bundle загружен): приватные опции подключения
`kCBConnectOptionRequiresLowLatency` / `LatencyCritical` / `OverrideMin/MaxCIFrames = 6` (по JamCon → 15 мс) + лог
фактического интервала из приватного делегата. ☐ Гц в F9, строка `connection interval … ms` в консоли, оба Joy-Con.

## 2026-10-13 · macOS 15.7.4 · Editor · после приватных опций подключения

Пользователь: «работает» — частота поднялась (Гц из F9 не записан; ожидаемо ~66 при 15 мс). По логу: оба Joy-Con
`connecting #N (low latency, CI frames 6)` → `ready after` 350 / 331 мс (было 1746 мс), L → P1, R → P2, калибровка factory.
Приватный делегат `didUpdateConnectionParameters` при первом подключении не пришёл — фактический интервал в лог не попал.
☐ Записать Гц из F9; проверить в билде `Kagefumi → Build macOS (dev)`; > 10 мин без обрывов.

## 2026-10-13 · macOS 15.7.4 · WebGL-билд (Фаза C) — сборка и smoke-тест, без Joy-Con

| Что | Результат |
|---|---|
| `Kagefumi → Build WebGL (dev, localhost)` на Mac (Unity 6000.3.18f1) | ☑ `Builds/WebGL`, 112 МБ, 218 с; предупреждение про `Joycon2Boost.exe` — ожидаемо (утилита только для Windows) |
| `python3 -m http.server 8000 -d Builds/WebGL` → встроенный браузер Claude | ☑ лобби, оверлей; `[Joycon2] started (Web Bluetooth)` |
| Chrome 155.0.8059.40 → `http://localhost:8000` | открыт, ☐ подключение Joy-Con — ждёт проверки на железе |

## 2026-10-13 · macOS 15.7.4 · Chrome 155 · два Joy-Con 2 в WebGL — подключение, частота, графика

Пользователь: оба Joy-Con подключаются, **имена в chooser нормальные** («Joy-Con 2 (L/R)»), L → P1, R → P2, калибровка
factory, гироскоп живой. **Частота ~32–33 Гц** (F9: L 31.8, R 33.0) — дефолтный интервал macOS 30 мс; Chrome
подключает без приватных опций, которые в Editor дают ~66 Гц.

**Баг, исправлен: на Mac в WebGL не рисовалась графика** (только UI и линии). В консоли сотни
`GL_INVALID_OPERATION: glDrawElements: Mismatch between texture format and sampler type (…shadow)`. Инструментировал
WebGL в странице: шейдер семплирует `_AdditionalLightsShadowmapTexture` shadow-сэмплером, а привязана текстура без
compare mode (и не depth: принудительный `TEXTURE_COMPARE_MODE` не помогает). Причина — URP 17.3: если источнику нечего
затенять (днём фонари выключены, ночью солнце), он всё равно включает keyword теней и биндит RenderGraph
`DefaultShadowTexture` (D32, без `CompareDepths`). ANGLE→Metal (Chrome на Mac) такой draw отклоняет, ANGLE→D3D11
(Windows) пропускает. Обход — `Assets/Kagefumi/Scripts/WebGLShadowmapFix.cs` (только WebGL-плеер): после shadow-проходов
подменяет 1×1 «пустую» карту на свою, созданную как настоящие (`ShadowUtils.AllocShadowRT`, D16). ☑ Встроенный Chromium
(Metal): день, сумерки, ночь — всё рисуется, тени есть, ошибок нет.

## 2026-10-13 · macOS 15.7.4 · Chrome 155 · частота в вебе — `joycon2boost.app` (эксперимент)

Цель — 60+ Гц в WebGL на Mac. Chrome подключает Joy-Con без приватных опций → интервал 30 мс (~33 Гц). Инструмент
`Native/macos/tools/joycon2boost.m` → `build/joycon2boost.app` (фоновое, без иконки в Dock; лог — stdout,
`open -n --stdout /tmp/jc2boost.log … --args --measure`). Частота мерилась подпиской на `…7fd2` в самой утилите и
совпадала с F9.

| Опыт | Результат |
|---|---|
| Присоединиться к уже созданному Chrome соединению (`retrieveConnectedPeripheralsWithServices` + `connectPeripheral` с опциями + `setDesiredConnectionLatency:0`) | ✖ 33 Гц |
| Утилита подключает первой (скан, опции), Chrome потом | ✖ 33 Гц ещё до Chrome; ✖ chooser Chrome уже подключённые устройства **не показывает** (ни с фильтром по имени, ни с `services:[…7fd0] + name`) |
| То же, но как обычное активное приложение (`--foreground`) | ✖ 33 Гц — дело не в «переднем плане» |
| Контроль: Editor Play | ☑ 60+ Гц (F9) — опции рабочие |
| Контроль: `joycon2probe.app` (ad-hoc, тот же bundle) | ☑ 65,8–66,8 Гц — дело не в подписи/процессе |
| Утилита без `setDesiredConnectionLatency:0` (bundle его по умолчанию не зовёт) | ☑ **66–67 Гц** на своём соединении. **Этот вызов сбрасывает переопределение CI** → по умолчанию выключен (`JC2_MAC_DESIRED_LATENCY`) |
| Скан во время подключения / возобновление скана после | не влияет |
| Присоединение к соединениям Chrome без этого вызова | ◐ **первый подключённый Joy-Con 33 Гц, второй 66–67** (дважды, опрос и раз в секунду, и 10 раз в секунду); повторное присоединение к медленному ничего не меняет |
| Переподключить первый (SYNC → снова выбрать в Chrome), пока утилита держит второй | ☑ **оба 66–67 Гц** |

**Вывод:** macOS фиксирует интервал при *создании* соединения и даёт 15 мс, если в этот момент в bluetoothd уже есть клиент
с низкой задержкой (наша утилита на другом Joy-Con). Присоединение к готовому соединению интервал не меняет. Рабочий путь
сейчас: утилита запущена → подключить оба Joy-Con в Chrome → первый переподключить → оба ~66 Гц.
☐ Сделать без ручного переподключения первого.

## 2026-10-13 · macOS 15.7.4 · Chrome 155 · 66 Гц в вебе — переподключение медленного и запуск утилиты из игры

- Автопереподключение медленного Joy-Con (`Joycon2Manager.RefreshSlowLinks`): игра закрывает соединение и через 1,5 с
  зовёт `gatt.connect()` — ✖ Joy-Con 2 после отключения хостом подключение **не принимает**, пока не нажать SYNC; и
  одного SYNC мало — Chrome подключил его только после повторного выбора в chooser. Поэтому: подсказка в игре
  («Speeding up Joy-Con 2 (L): hold SYNC on it — … pick it again»), chooser открывается во время переподключения, выбор того
  же Joy-Con продолжает его сессию (игрок и gamepad те же). ☑ Пользователь: «работает, приходится кликать самому».
- `joycon2boost.app` — агент (без Dock), `joycon2boost://` в Info.plist, лог `~/Library/Logs/joycon2boost.log`, выход
  после 10 мин без Joy-Con. ☑ `open joycon2boost://start` запускает, второй URL попадает в тот же процесс. ☑ Из игры: B →
  Chrome «Открыть joycon2boost?» → утилита запущена, второй Joy-Con сразу ~66 Гц, первый — после переподключения по подсказке.
- Скачивание `Joycon2Boost-mac.zip` из игры (вторая B) — ☐ не проверено (утилита уже была зарегистрирована).

## 2026-10-13 · Android APK · сборка и статическая проверка, без телефона/Joy-Con

Целевой телефон, **со слов пользователя**: Pixel 3, Android 12. По `adb devices` устройство не подключено;
запуск APK, разрешения и реальные BLE-соединения на нём в этой сессии **не проверялись**.

| Проверка | Результат |
|---|---|
| Собственный Java BLE-транспорт, `Native/android/build.ps1` | ☑ AAR 18 244 байта, Unity OpenJDK + SDK 36, без сторонних зависимостей/предупреждений javac |
| Unity Editor 6000.3.18f1, Pipeline CLI; скрипты | ☑ компиляция без ошибок; Android platform-specific код также прошёл IL2CPP |
| EditMode, фильтр `Joycon2` | ☑ **43/43**: существующее ядро и 6 новых JNI-frame случаев (signed bytes, MAC/RSSI, короткий buffer, повреждённые заголовки/длины) |
| Android Build Support, переключение WebGL → Android | ☑ target Android, ARM64 IL2CPP, landscape, Development; scene Kagefumi |
| Первый APK | ☑ сборка 519 с, 0 ошибок; первый прогон компилировал шейдеры и нативный player |
| Окончательный APK с callback закрытия/permanent denial permission dialog | ☑ сборка 47 с, **0 ошибок**, 2 предупреждения про необязательный runtime Pipeline config и crash debug symbols |
| `Native/android/verify-apk.ps1` | ☑ APK Signature Scheme v2; один signer; Java `BleTransport` в DEX; ARM64 `libil2cpp.so` / `libunity.so`; BLE permissions в итоговом manifest |
| Итоговый файл | `Builds/Android/Kagefumi-Joycon2-dev.apk`, **54 330 848 байт (51,8 MiB)** |
| App id / SDK | `com.vivoderin.kagefumi`, version `0.1.0` / code 1, min SDK 25, target SDK 36; Bluetooth SCAN/CONNECT для Android 12+, legacy/Location ограничены SDK 30 |

SHA256 APK: `b528a22f2ba3e8dabcb5daf70f2051c99d160db4255a87dfb1d475af5c7bd964`.
Локальные отчёты: `Logs/Android-build-report.json`, `Logs/Joycon2-tests.json`,
`Builds/Android/verification.txt` (генерируемые файлы, не в Git).

Реализованы permission flow до старта менеджера, отказ/закрытие диалога, App/Bluetooth settings,
остановка BLE при паузе и скан после возврата, touch Details/Record/Connect-retry, сохранение исходных
notifications без изменения framing, HIGH priority на обоих соединениях. Всё это пока проверено сборкой;
поведение на Pixel 3 требует отдельного прогона.

**Дальше на телефоне:** установить APK → разрешить «Устройства поблизости» → SYNC на каждом Joy-Con →
Details: два отдельных gamepad, калибровка, длина репорта, MTU, Гц обоих → кнопкой на каждом зайти в игру →
стики/кнопки/LED/гироскоп/SUN GRAB; 10 минут; выключение Bluetooth и Joy-Con; пауза/возврат с повторным SYNC.
При проблеме — Record/Stop recording и логкат `Unity` / `Joycon2Ble`. Дампы содержат реальные байты:
сначала сверить их с нашим Win/Mac, затем менять ядро только по данным.

## 2026-10-13 · Pixel 3 · Android 12 · нативный APK — проверка пользователем

После передачи `Kagefumi-Joycon2-dev.apk` пользователь сообщил: **«отлично работает»** и разрешил
закоммитить/запушить реализацию. Общая работоспособность Android APK с Joy-Con подтверждена пользователем.

Частота каждого контроллера, MTU, прошивки, длительность игры и отдельные результаты для LED,
гироскопа/SUN GRAB, Bluetooth off/on, паузы/возврата и переподключения не сообщались.
Эти пункты остаются открытыми; конкретные цифры и прохождение 10-минутного теста из этого сообщения
не выводим. Собственных логов/дампов с телефона в этой сессии нет.
