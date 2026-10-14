# JoyconControllerTest

Unity 6 (6000.3.18f1, URP, Input System 1.19) проект-эксперимент: можно ли подключить **два Joy-Con 2
(Nintendo Switch 2)** к игре как **два независимых геймпада** — на Windows, macOS, Android, iOS и в WebGL
(Chrome/Edge на Win/Mac/Android, Bluefy на iOS/iPadOS).

Тестовая игра — KAGEFUMI, дуэль на двоих: `Assets/Kagefumi/`. Раздача устройств игрокам —
`Assets/Kagefumi/Scripts/KagefumiGame.cs` (любое устройство `Gamepad` попадает в схему `"Gamepad"`).
Сцену собирает `Assets/Kagefumi/Editor/KagefumiSceneBuilder.cs`.

## Документация (читать по необходимости, не целиком заранее)

- [Docs/Joycon2/PLAN.md](Docs/Joycon2/PLAN.md) — план, архитектура, фазы и их статусы, риски.
  Читать **перед любой работой с Joy-Con / BLE / Input System / нативными плагинами / WebGL**.
- [Docs/Joycon2/PROTOCOL.md](Docs/Joycon2/PROTOCOL.md) — UUID, команды, разметка input report, квирки
  платформ. Читать, когда пишешь/правишь парсер, handshake или транспорт.
- `Docs/Joycon2/TEST_LOG.md` — результаты проверок на железе (создаётся по ходу работ).

## Правила

- Joy-Con 2 — это BLE с проприетарным GATT, **не** HID: ОС, Unity Input System и Gamepad API браузера
  сами их не видят. Не пытаться «просто включить поддержку» в настройках.
- Протокол и handshake — только в общем C#-ядре; нативные плагины/`.jslib` — тонкий транспорт байтов.
- После завершения шага — обновить статус в PLAN.md и, если что-то проверено на железе, TEST_LOG.md.
- Исходники нативных плагинов — в `Native/` (вне `Assets/`), собранные бинарники — в `Assets/Joycon2/Plugins/`.
