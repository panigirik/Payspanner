#!/bin/sh
# Гигиена движка: имени кассы нет в src/. Движок обязан не знать, кого он
# обслуживает: имя живёт в integrations/<имя>.json, её тестах и доках.
# Секреты проверяет tools/scan-secrets.sh. Оба зовёт tools/verify.sh.
# Один grep на проверку: построчный запуск grep на Windows занимает минуты.
set -eu
cd "$(dirname "$0")/.."

names=$(
  {
    find integrations -maxdepth 1 -type f -name '*.json' -exec basename {} .json \;
    sed -e 's/\r$//' -e '/^$/d' tools/leftover-names.txt
  } | tr 'A-Z' 'a-z' | sort -u | paste -sd '|' -
)

if [ -z "$names" ]
then
  exit 0
fi

hits=$(
  grep -R -n -o -E -i \
    --include='*.cs' --include='*.json' \
    --exclude-dir=bin --exclude-dir=obj \
    -e "($names)" \
    src 2>/dev/null || true
)

report=$(printf '%s\n' "$hits" | awk '
  NF == 0 { next }
  {
    a = index($0, ":")
    rest = substr($0, a + 1)
    b = index(rest, ":")
    key = substr($0, 1, a + b - 1)
    if (seen[key]++) next
    found = substr(rest, b + 1)
    gsub(/\r/, "", found)
    printf "%s:%s: provider name '\''%s'\'' in src/\n", substr($0, 1, a - 1), substr(rest, 1, b - 1), found
  }
')

if [ -n "$report" ]
then
  printf '%s\n' "$report"
  echo "hygiene: $(printf '%s\n' "$report" | grep -c .) problem(s)"
  exit 1
fi

exit 0
