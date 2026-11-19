#!/bin/sh
# Единый гейт: сборка, тесты, секреты, гигиена, формат определений. Его зовут pre-push
# (tools/hooks/pre-push) и tools/verify.cmd - других версий проверки нет.
#
set -eu

cd "$(dirname "$0")/.."

echo "verify: build"
dotnet restore PayEngine.slnx
dotnet build PayEngine.slnx -c Release --no-restore -warnaserror

# Без --filter: на Microsoft Testing Platform VSTest-фильтр даёт
# «Zero tests ran», а живые тесты и так пропускаются без переменной.
echo "verify: test"
dotnet test --solution PayEngine.slnx -c Release --no-build

echo "verify: secrets"
sh tools/scan-secrets.sh

echo "verify: hygiene"
sh tools/check-hygiene.sh

# integrations/*.json в одном виде. Скрипт проверяет файл и не переставляет тело запроса.
echo "verify: definitions"
python=$(command -v python3 || command -v python)
"$python" -I tools/format-configs.py --check

echo "verify: build, test, secrets, hygiene, and definitions passed"
