# PayEngine

Учебный движок. Интеграция с платёжным провайдером задаётся JSON-конфигом.

Схема — [docs/SCHEMA.md](docs/SCHEMA.md). Образец — [integrations/example.json](integrations/example.json).

Нужен .NET SDK 10. Проект собирается в `net8.0`. `global.json` фиксирует SDK 10: на SDK 8 команда `dotnet test --solution` не запускается.

Проверка из корня: `sh tools/verify.sh`. На Windows — `tools\verify.cmd`.
