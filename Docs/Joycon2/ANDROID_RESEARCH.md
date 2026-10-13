# Joy-Con 2 в Android APK — исследование

Дата: 2026-10-13. Исходная версия нашего проекта: `9512af2`.
Статус исследования: завершено. Позднее в тот же день Android-транспорт реализован;
сборка и аппаратные проверки отслеживаются в [PLAN.md](PLAN.md), инструкция — [Native/android/README.md](../../Native/android/README.md).
Текст ниже фиксирует исходное исследование; чужие аппаратные результаты не считаются нашими.

## Вывод

Нативная поддержка двух отдельных Joy-Con 2 внутри KAGEFUMI выглядит практически реализуемой.
Есть публичные Android-реализации и опубликованные результаты одновременной работы двух контроллеров.
Рекомендуемый путь: собственный небольшой Java AAR с BLE-транспортом, C#-обёртка
`IJoycon2Transport`, существующие `Joycon2Session` и `Joycon2Gamepad`.
Root, Shizuku и системный виртуальный HID для этого пути не нужны: геймпады создаются в Input System
нашей игры. Это вывод из нашей архитектуры и разделения BLE / системного вывода в чужих проектах,
а не результат запуска нашего Android APK.

## Публичные проекты

### JoeGeC/joycon2android

[Репозиторий](https://github.com/JoeGeC/joycon2android), GPL-3.0;
проверенный commit `b8724b5c0905d87dc9b3a850ad0ef5632cc758c4`.
Есть [готовый APK v2.0.0](https://github.com/JoeGeC/joycon2android/releases/tag/v2.0.0).
Это Android-приложение на Kotlin: несколько контроллеров, отдельный геймпад на игрока,
боковой хват, гироскоп/акселерометр через DSU. Shizuku используется для вывода геймпада в другие
приложения; получение BLE-данных — отдельный слой. Полезный эталон для проверки телефона и контроллеров.
Код GPL не выбираем для включения в наш AAR; изучаем поведение и пишем свой транспорт.

В нашем старом PLAN указан [SWeav02/joycon2android](https://github.com/SWeav02/joycon2android).
Это форк JoeGeC, а не независимая реализация. Для актуального исследования использован upstream.

[Документация протокола и Android-опыт](https://github.com/JoeGeC/joycon2android/blob/b8724b5c0905d87dc9b3a850ad0ef5632cc758c4/docs/protocol.md)
описывает на AYN Thor / Android 13 около 33 Гц при balanced и около 67 Гц при high priority.
Также объясняет необходимость SYNC для каждого нового подключения: приватный адрес Android мешает
повторить пробуждение к сохранённому хосту. В MVP оставляем SYNC, без записи нового хозяина контроллера.
Обходы SMP и L2CAP в разделе про сторонние клоны не переносим на оригинальные Nintendo Joy-Con 2.
Эксперименты со скрытым API и интервалом 7,5 мс относятся к отдельному console-path; для первого APK
ориентируемся на публичный HIGH и измеренные 60+ Гц, без обещаний конкретной частоты на любом телефоне.

Особенно полезны [CommonSession.kt](https://github.com/JoeGeC/joycon2android/blob/b8724b5c0905d87dc9b3a850ad0ef5632cc758c4/feature/connection/data/src/main/kotlin/com/joegec/joycon2android/connection/CommonSession.kt)
и [GattOpQueue.kt](https://github.com/JoeGeC/joycon2android/blob/b8724b5c0905d87dc9b3a850ad0ef5632cc758c4/feature/connection/data/src/main/kotlin/com/joegec/joycon2android/connection/GattOpQueue.kt):
последовательные операции, завершение через callback, таймауты, CCCD, write-without-response,
ветки старого и нового Android API. Протокольную последовательность оставляем нашей.

### patrickmerkel26-dev/Joycon-2-Android-Bridge

[Репозиторий](https://github.com/patrickmerkel26-dev/Joycon-2-Android-Bridge), MIT;
проверенный commit `8e2d2e2563e04d585841d6026332ab31a684006f`.
Java-приложение с двумя независимыми BLE-сессиями; в системном выводе объединяет L/R в один пад.
Готового Unity AAR в нём нет. Если заимствовать код, сохранить MIT notice и атрибуцию.

В [VERIFICATION.md](https://github.com/patrickmerkel26-dev/Joycon-2-Android-Bridge/blob/8e2d2e2563e04d585841d6026332ab31a684006f/docs/VERIFICATION.md)
есть запись реального двойного прогона: более 29 000 кадров, примерно 68 Гц и 62-байтные репорты.
README указывает Galaxy S24 Ultra. Автор также зафиксировал ошибку определения стороны и фантомные
кнопки из-за ошибочного удаления `0xA1`, когда этот байт оказался частью счётчика.
Это чужая проверка, не наша; её нельзя переносить в наш TEST_LOG как успешный Android-тест.

### Nordic Android BLE Library

[Репозиторий](https://github.com/nordicsemi/Android-BLE-Library), BSD-3-Clause;
проверенный commit `da698f297c75784ca3932bc19777cde93ec7c975`.
Общая библиотека BLE с очередью операций, таймаутами, MTU и приоритетом; сама Joy-Con 2 не поддерживает.
Вариант для более широкого набора Android-устройств, если собственный транспорт обрастёт обработкой
квирков. Для первого эксперимента предлагается обычный Android API, чтобы не добавлять зависимости
и их настройку в Unity. Это проектное решение, не ограничение библиотеки.

Готового проверенного Unity-плагина именно для Joy-Con 2 на Android в проведённом поиске не найдено.
Это не утверждение, что такого плагина нигде не существует.

## Что переиспользуем из Windows/macOS

| Уже есть в проекте | Польза для Android |
|---|---|
| `Core/Joycon2Protocol.cs`, `Joycon2Session.cs` | UUID, handshake, чтение user/factory-калибровки, LED, feature mask `0x37`, keep-alive |
| `Joycon2Report`, mapping, motion, gyro bias, orientation | Кнопки, стики, боковой хват, поза и SUN GRAB |
| `IJoycon2Transport`, `Joycon2TransportEvent` | Готовый контракт scan/connect/subscribe/write и событий |
| `Joycon2Manager` | Два устройства, back-off, таймаут подключения, создание/удаление Input System gamepad |
| Windows worker / macOS serial queue | Образец владения состоянием и очереди сырых событий; Android требует своей реализации |
| `Joycon2DebugOverlay`, `RawReport` | Частота, состояние handshake, дампы для сравнения платформ |
| `TEST_LOG.md` | Эталон ожидаемого поведения и реальных ограничений частоты |

Windows DLL и macOS bundle на Android не загружаются. Переносятся контракт и логика, а не бинарники.
JNI через `AndroidJavaObject` может заменить C ABI для Android, сохранив `IJoycon2Transport`.
[Unity 6000.3 поддерживает импорт AAR](https://docs.unity3d.com/6000.3/Documentation/Manual/android-aar-import.html)
и [вызовы Java из C#](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AndroidJavaObject.html).
Отдельный C++/NDK-мост для BLE не требуется.

## Предлагаемое устройство транспорта

Исходники в `Native/android/`, собранный `joycon2ble.aar` в `Assets/Joycon2/Plugins/Android/`.
В AAR нет Nintendo-команд и парсера. C# передаёт company id, UUID и байты.

1. Один Android worker/Handler владеет состоянием; отдельная очередь GATT-операций на каждый handle.
2. `BluetoothLeScanner` фильтрует manufacturer company id; в `DeviceFound` отдаёт исходные manufacturer
   bytes без company id, адрес и RSSI. PID и SYNC разбирает существующий C#.
3. `connectGatt(autoConnect=false, TRANSPORT_LE)`; MTU negotiation и discovery заканчиваются до
   события `Connected`. C# не начинает handshake на незавершённом GATT.
4. `Subscribe` включает локальные notifications и пишет CCCD `0x2902`;
   `Subscribed` появляется после успешного descriptor callback.
5. `Write` использует `WRITE_TYPE_NO_RESPONSE`; успешная постановка в Android API не означает ACK
   Nintendo-команды. ACK по-прежнему ждёт `Joycon2Session`.
6. Notifications копируются в ограниченную очередь. C# забирает их polling-ом на главном потоке;
   Unity API из Android callbacks не вызывается. Первую версию можно сделать с одним вызовом на событие,
   затем измерить выделения памяти и при необходимости забирать события пачкой.
7. Старые callbacks после disconnect/shutdown отсеиваются по экземпляру GATT / поколению сессии.
   Таймаут операции не должен позволять её позднему callback завершить следующую операцию.

## Android-особенности и проверяемые риски

**MTU.** [Android BluetoothGatt](https://developer.android.com/reference/android/bluetooth/BluetoothGatt#requestMtu(int))
позволяет запросить увеличенный MTU; с Android 14 первый запрос инициирует 517, дальнейшие игнорируются.
Нужно ждать `onMtuChanged` и учитывать согласованное значение, а не требовать ровно 517.
Для нашего ядра важны и репорты, и ответы калибровки: factory-read `0x49` плюс 16 байт заголовков =
89 байт, то есть для него нужен ATT MTU минимум 92. Запрос 517 — подходящий максимум;
247 из чужих реализаций тоже достаточен. MTU 23 оставляет 20 байт payload: наш парсер примет кнопки/стик,
но потеряет IMU и полный ответ памяти. Это надо показывать как проблему, а не как полный успех.

**Частота.** [requestConnectionPriority](https://developer.android.com/reference/android/bluetooth/BluetoothGatt#requestConnectionPriority(int))
доступен публично. Просим HIGH на обоих соединениях и измеряем оба потока после подключения второго.
Возврат `true` означает принятие запроса, не гарантирует 15 мс. Частота BLE и FPS игры проверяются отдельно.
Приватные ключи CoreBluetooth и WinRT API сюда не переносятся.

**Разрешения.** [Android permissions](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions):
API 31+ — runtime `BLUETOOTH_SCAN` и `BLUETOOTH_CONNECT`; API 25–30 — legacy Bluetooth permissions
и runtime `ACCESS_FINE_LOCATION`. `neverForLocation` соответствует назначению нашей игры, но Android
предупреждает о фильтрации части BLE-рекламы — проверить реальное обнаружение Joy-Con.
На старых устройствах также проверить включённую системную геолокацию.

**Запуск.** Сейчас `Joycon2Manager.CreateTransport()` на Android выбирает NullTransport.
При неуспешном `Initialize()` менеджер завершает запуск и сам не ждёт выдачи разрешений.
Нужен асинхронный permission flow с повторным `Start()` после разрешения, состоянием отказа и повторной
проверкой после возврата из настроек; добавление одной ветки `UNITY_ANDROID` недостаточно.

**Пауза приложения.** `Joycon2Host` обрабатывает только quit/destroy. В фоне Unity может остановить
polling и наш C# keep-alive; разрешения тоже могут отозвать. Для MVP явно останавливаем BLE при уходе
в фон и восстанавливаем возможность скана при возвращении, с подсказкой SYNC. Длительная игра в фоне
и foreground service — отдельная задача.

**Формат данных.** Сохраняем сырые bytes. Наш парсер уже допускает 62/63/64 байта и читает IMU при
длине от 60; расширять длину или удалять первый байт наугад не нужно. Сверить дампы Android с Win/Mac.
У JoeGeC другие feature mask и масштаб gyro; не заменять наш `0x37`/масштаб без измерения.
Представление кнопок с offset `0x03` и сдвинутыми масками само по себе не доказывает другой формат:
у нас offset `0x04`, и соответствующие маски дают те же кнопки.

**Диагностика на телефоне.** Нужны touch-кнопки записи дампа и видимости оверлея: F9/F10 без
клавиатуры недоступны. Выводить GATT status, MTU, длину репорта, время handshake и Гц отдельно L/R.

## Следующий эксперимент

В установленной Unity 6000.3.18f1 уже есть AndroidPlayer, SDK (platforms 34/35/36), Build Tools 36.0.0,
OpenJDK и NDK. В настройках проекта min SDK 25 и ARM64. Наличие инструментов проверено,
сборка Android этого проекта в рамках исследования не выполнялась.

Рекомендуемый порядок реализации:

1. AAR-транспорт + C# wrapper, разрешения, диагностический Development APK KAGEFUMI.
2. Один оригинальный Joy-Con: SYNC → GATT → handshake → factory/user calibration → gamepad;
   проверить кнопки, стик, LED, gyro и длину пакета.
3. Второй Joy-Con: отдельный gamepad и игрок, измерить частоту каждого, пройти SUN GRAB.
4. 10 минут игры; выключение контроллера; Bluetooth off/on; отказ/выдача разрешений;
   пауза/возврат; переподключение через SYNC.
5. Записать модель телефона, Android, прошивку Joy-Con, MTU, Гц и фактические результаты в TEST_LOG.

До нашего аппаратного теста оценка такая: высокая уверенность в получении двух BLE-потоков;
разумное ожидание 60+ Гц на современном телефоне; совместимость всех Android-моделей и поведения
при возврате из фона остаётся открытой. Основная работа — транспорт и жизненный цикл,
а не новый реверс Nintendo-протокола.
