# Joy-Con 2 в KAGEFUMI WebGL на Android

Исследование: 2026-10-13. Проверены исходники проекта, документация Unity 6000.3,
Web Bluetooth и Android-путь подключения Chromium. На телефоне браузерная версия
в рамках этого исследования **не проверялась**. Работа APK на Pixel 3 / Android 12
уже подтверждена пользователем (TEST_LOG), но это другой транспорт.

## Вывод

**Реализуемо через Web Bluetooth в Chrome на Android.** Для подключения не нужны
новый Nintendo-протокол, системный виртуальный геймпад или перенос AAR в браузер.
У нас уже есть нужные C#-ядро и Web Bluetooth `.jslib`, проверенные с двумя Joy-Con
в Chrome на Windows и macOS. Основная доработка — управление подключением с экрана,
сообщения об условиях запуска и аппаратная проверка мобильного браузера.

Уверенность в архитектурной возможности высокая. Работа обоих Joy-Con именно
в нашей игре на Android Chrome, частота ≥ 60 Гц, стабильность и скорость рендеринга
пока не подтверждены. Их нельзя выводить только из успеха APK или десктопного веба.

## Что подтверждают первоисточники

- [Chrome: Web Bluetooth](https://developer.chrome.com/docs/capabilities/bluetooth):
  Android поддерживает подключение BLE GATT, записи и notifications; нужны secure
  context и жест пользователя. Один выбор устройства возвращает один контроллер.
- [Статус реализации Web Bluetooth](https://github.com/whatwg/bluetooth/blob/main/implementation-status.md):
  GATT API на Android Chrome работает без экспериментальных флагов, manufacturer
  filters доступны с Chrome 92. Android WebView указан среди неподдерживаемых
  платформ. Для первого теста используем актуальный обычный Chrome.
- [Unity 6000.3: совместимость браузеров](https://docs.unity3d.com/6000.3/Documentation/Manual/webgl-browsercompatibility.html):
  Chrome на Android входит в поддерживаемые мобильные браузеры Unity Web.
  Это поддержка движка, а не гарантия FPS нашего URP-проекта.
- [Демо Joy-Con 2 автора mascii](https://github.com/mascii/joy-con-2-web-bluetooth-api-demo):
  эталон соединения с Nintendo GATT, отдельные кнопки L/R;
  [живое демо](https://joy-con-2-web-bluetooth-api-demo.pages.dev/).
  README перечисляет Chrome/Edge/Bluefy, но не сообщает результаты двух Joy-Con
  на конкретном Android-телефоне или их частоту — такого подтверждения не заявляем.

## Что уже есть в проекте

| Участок | Состояние и использование на Android Web |
|---|---|
| `Runtime/Input/Joycon2Manager.cs`, `CreateTransport` | WebGL выбирает `Joycon2WebBluetoothTransport`; Java-транспорт APK в этот билд не попадает |
| `Plugins/WebGL/Joycon2WebBluetooth.jslib` | `requestDevice` → GATT → последовательные subscribe/write → очередь сырых notifications → polling из C#; хранит несколько независимых устройств |
| `Joycon2Manager.BuildDeviceRequest` | Nintendo company `0x0553`, PID L/R, маска manufacturer data, `optionalServices` с Nintendo UUID; повторно используем |
| `Core/Joycon2Session.cs` | Определение L/R при неизвестном имени, калибровка, handshake, LED, `0x37`, keep-alive; остаётся общим |
| `Input/Joycon2Gamepad.cs` и `KagefumiGame.Join` | Каждый контроллер — отдельный Gamepad, отдельный игрок; сохраняем существующий путь кнопок, стика и IMU/SUN GRAB |
| `KagefumiWebGLBuild.cs` | Уже собирает Development WebGL в `Builds/WebGL`; отдельный Android WebGL backend не требуется |

Десктопный пример помогает напрямую: используем тот же WebGL-транспорт и C#-ядро.
Android APK дополнительно подтверждает совместимость наших контроллеров и телефона
с этим протоколом, но его разрешения, MTU и connection priority реализованы через Java.

## MTU и частота: два разных вопроса

Проверен [ChromeBluetoothDevice.java](https://github.com/chromium/chromium/blob/45db9d00dd51d99fa811daca9613640d0390654f/device/bluetooth/android/java/src/org/chromium/device/bluetooth/ChromeBluetoothDevice.java)
(последнее изменение файла в проверенном `main`: commit
`45db9d00dd51d99fa811daca9613640d0390654f`, 2026-07-10).
После соединения Chromium вызывает `requestMtu(517)`, затем выполняет discovery
из `onMtuChanged`; если запрос сразу не принят, переходит к discovery.
Это хороший признак для полного input (~63 байта) и нашего самого длинного ответа
калибровки (89 байт, ATT MTU ≥ 92). Успешное согласование и конкретное значение
не гарантированы. Проверенный `main` не доказывает версию кода в установленном Chrome.

В этом классе, `BluetoothGattWrapper.java` и `bluetooth_device_android.cc` не найден
вызов `requestConnectionPriority(CONNECTION_PRIORITY_HIGH)`. Наш
`Native/android/src/com/vivoderin/joycon2/BleTransport.java` вызывает его явно.
Следовательно, переносить ожидание частоты APK на Chrome нельзя. У Web Bluetooth
нет публичного метода для установки ATT MTU или connection interval/priority.
Размер пакета сам по себе не определяет частоту.

Что измерять:

- Реальную длину notifications и наличие IMU, а не только движение стика:
  наш парсер допускает короткие пакеты для кнопок/стиков, поэтому частичная работа
  ещё не доказывает полный поток. Для IMU нужно минимум 60 байт.
- Гц отдельно L/R сначала с одним, затем с двумя подключёнными контроллерами.
  MTU в веб-оверлее сейчас неизвестен (`0`), браузер его не отдаёт.
- Равномерность поступления, задержку SUN GRAB, FPS и нагрев телефона отдельно
  от BLE-частоты. Просадки FPS могут задерживать polling очереди.

Если связь будет медленной: сначала собрать фактические Гц/длины/логи. Windows EXE
и macOS `.app` на Android не применимы. Нативный Android helper, запрашивающий HIGH
на общем физическом соединении, — возможный отдельный эксперимент, а не готовое
решение на момент исследования. Позднее, 2026-10-14, Android-helper v0.2 подтвердил
это на Pixel 3: оба общих соединения 15 мс и 60+ Гц в Chrome (TEST_LOG).
Он требует установки приложения. Также запись Nintendo-дескриптора частоты не гарантирует
уменьшение интервала BLE. Для чисто браузерного варианта ≥ 60 Гц пока открытый вопрос.

## Доработки для телефона

1. **Явные кнопки подключения L/R.** В `KagefumiGame.OnAnyButtonPress` сейчас
   обрабатываются `Mouse.leftButton` и `Keyboard.jKey`; `Touchscreen` явно не
   обрабатывается. Эмуляцию мыши касанием считать гарантированной нельзя.
   Добавить поддержку касания либо экранные кнопки, вызывающие существующий
   `Joycon2Manager.RequestController(side)`. Chooser для каждого Joy-Con открывать
   отдельным жестом; не пытаться открыть второй из завершения первого Promise.
   При необходимости `.jslib` уже умеет ждать следующий DOM `pointerup`/`keyup`.
   Для открытия за один тап надёжнее прямой DOM-обработчик: C# передаёт параметры
   выбора заранее, JS вызывает API внутри жеста, результат возвращается через
   существующий polling. Nintendo-handshake в JS не переносим.
2. **Диагностика без клавиатуры.** Добавить Details/Record для WebGL на телефоне,
   масштабирование оверлея и экспорт дампа из браузера. `Joycon2AndroidControls`
   и увеличение шрифта под `UNITY_ANDROID` относятся к APK; в WebGL их нет.
   F9/F10 и сообщения «press J» на телефоне недостаточны.
3. **Понятный отказ до подключения.** Показать в странице/UI причины: нет HTTPS,
   нет `navigator.bluetooth`, доступ запрещён, Bluetooth выключен, выбор отменён.
   Сейчас ошибку запуска транспорта видно прежде всего в консоли, а оверлей
   создаётся только при успешной инициализации менеджера.
4. **Мобильный WebGL.** Стандартный шаблон уже содержит мобильный viewport.
   Для первого прогона ограничить `devicePixelRatio` (например, 1) и проверить
   landscape/размеры UI. Последний локальный dev-build и дампы в TEST_LOG относятся
   к десктопным сборкам; оптимизацию загрузки/URP делать по измерениям телефона.
5. **Фон и возврат.** При скрытии страницы/блокировке экрана Chrome может замедлить
   Unity и keep-alive. Ограничение очереди в `.jslib` уже есть, но оно не обеспечивает
   стабильную игру в фоне. Проверить chooser, переключение приложения и блокировку;
   при возвращении после обрыва дать снова SYNC + выбор, без дублей Gamepad и зажатых кнопок.

Первые изменения затрагивают UI/браузерный транспорт и шаблон страницы.
Новые нативные библиотеки или изменения парсера до появления аппаратных данных не нужны.

## Условия запуска на Android

- Открывать игру напрямую в Chrome по **HTTPS с доверенным сертификатом**.
  `http://192.168.x.x:8000` с компьютера не заменяет secure context; `localhost`
  на телефоне означает сам телефон. Для разработки возможен USB `adb reverse`
  с открытием телефонного `http://localhost:8000`, либо доверенный HTTPS-хост.
- Bluetooth включён; разрешить Chrome доступ к устройствам. Android 12+ использует
  Nearby devices, старые версии — location для BLE scan; конкретные диалоги зависят
  от Chrome/Android. Эти разрешения получает браузер, сайт не вызывает
  `UnityEngine.Android.Permission` и не использует manifest нашего AAR.
  [Модель разрешений Android](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions).
- Зажать SYNC, выбрать контроллер в chooser, повторить отдельным тапом для второго.
  Не спаривать Joy-Con через системные настройки; отключить использующий их APK/Switch.
- Для размещения в cross-origin iframe родитель должен разрешить Bluetooth
  (`allow="bluetooth"` и совместимая Permissions Policy). Для первого теста открыть
  страницу напрямую. [Web Bluetooth: policy](https://github.com/whatwg/bluetooth/blob/main/index.bs#permissions-policy).

## Порядок аппаратной проверки

1. Pixel 3 / Android 12, актуальный Chrome: живое демо mascii, оба Joy-Con.
   Это отделит доступ браузера к BLE от загрузки Unity; демо не доказывает правильный
   маппинг/feature mask нашего ядра.
2. Наш Development WebGL по HTTPS/телефонному localhost: один Joy-Con, затем два;
   отдельные устройства/игроки, кнопки, стик, LED, калибровка, gyro/SUN GRAB.
3. Записать длины, частоту обоих и FPS; 10 минут игры; выключение контроллера и
   Bluetooth, отказ/повторная выдача доступа, reload, фон/возврат, повторное подключение.
4. В TEST_LOG внести Android/Chrome, модель телефона, прошивки (если известны),
   способ размещения, Гц L/R, длины, стабильность и дампы. До этого Фазу E не считать
   подтверждённой на железе.

Исследование завершено. В последующем шаге 2026-10-14 реализованы мобильные DOM-кнопки
Connect L/R, Details/Record/Download, сообщения отказа, управление связью при уходе в фон
и адаптивный WebGL-шаблон. Nintendo-протокол остаётся в общем ядре.
Инструкция запуска — [Native/web/README.md](../../Native/web/README.md), актуальный статус —
Фаза E в PLAN. Аппаратная проверка Android Chrome остаётся открытой.
