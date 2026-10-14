# JoyconControllerTest

Unity 6 (6000.3.18f1, URP, Input System 1.19). Здесь живёт UPM-пакет **`com.vivoderin.joycon2`** — два Joy-Con 2
(Nintendo Switch 2) как два независимых геймпада на Windows, macOS, Android, iOS и в WebGL (Chrome/Edge, Bluefy на iOS) —
и тестовая игра, которая его использует.

- Пакет: `Packages/com.vivoderin.joycon2/` (embedded). Другие игры подключают его по git URL — см. README пакета.
- Тестовая игра KAGEFUMI (дуэль на двоих): `Assets/Kagefumi/`. Раздача устройств игрокам —
  `Assets/Kagefumi/Scripts/KagefumiGame.cs` (любое устройство `Gamepad` попадает в схему `"Gamepad"`); сцену собирает
  `Assets/Kagefumi/Editor/KagefumiSceneBuilder.cs`; меню сборки под платформы — `Assets/Kagefumi/Editor/Kagefumi*Build.cs`.
- `Tools/` — скрипты деплоя тестовой игры (iOS на телефон, проверка APK).

## Документация (читать по необходимости, не целиком заранее)

- [README пакета](Packages/com.vivoderin.joycon2/README.md) — установка, API, настройки, платформы, пересборка плагинов.
- [PLAN.md](Packages/com.vivoderin.joycon2/Documentation~/PLAN.md) — архитектура, фазы и их статусы, риски.
  Читать **перед любой работой с Joy-Con / BLE / Input System / нативными плагинами / WebGL**.
- [PROTOCOL.md](Packages/com.vivoderin.joycon2/Documentation~/PROTOCOL.md) — UUID, команды, разметка input report, квирки
  платформ. Читать, когда пишешь/правишь парсер, handshake или транспорт.
- [TEST_LOG.md](Packages/com.vivoderin.joycon2/Documentation~/TEST_LOG.md) — результаты проверок на железе.
- [CHANGELOG.md](Packages/com.vivoderin.joycon2/CHANGELOG.md) — изменения пакета по версиям.

## Правила

- Joy-Con 2 — это BLE с проприетарным GATT, **не** HID: ОС, Unity Input System и Gamepad API браузера
  сами их не видят. Не пытаться «просто включить поддержку» в настройках.
- Протокол и handshake — только в общем C#-ядре (`Runtime/Core`, без UnityEngine); нативные плагины и `.jslib` — тонкий
  транспорт байтов с общим C ABI (`Native~/windows/include/joycon2ble.h`).
- Пакет ничего не знает про KAGEFUMI: никаких ссылок на игру, её сцены, bundle id и меню в `Packages/`. Всё, что нужно
  любой игре для сборки (Info.plist, Bluetooth usage, link.xml, помощники), делает `Editor/Joycon2BuildProcessor.cs`.
- Исходники нативных плагинов — в `Native~/`, собранные бинарники — в `Runtime/Plugins/`, помощники частоты для веба —
  в `WebHelpers~/`. Скрипты сборки сами кладут результат куда нужно.
- Тесты: EditMode `Joycon2.Tests.Editor` (Test Runner) и `node --test Native~/web/tests/web-controls.test.cjs` (из папки
  пакета). Новое поведение — с тестом.
- Публичный API пакета меняется осознанно: запись в CHANGELOG.md и версия в `package.json` (SemVer).
- После завершения шага — обновить статус в PLAN.md и, если что-то проверено на железе, TEST_LOG.md.
