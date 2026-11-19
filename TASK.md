# Тестовое: PaySnapper

Контекст: движок описывает интеграцию с платёжным провайдером («кассой»)
JSON-конфигом. Схема — docs/SCHEMA.md.
Образец — integrations/example.json и tests/fixtures/scheme.json.

Сделать:
1. integrations/paysnapper.json по документации кассы и брифу ниже.
2. Объект кассы в appsettings.example.json: Enabled и пустые секреты.
3. Файл кейсов tests/Cases/PaySnapper.json:
   каждый ответ create из доки, отказ, колбэк на каждый статус.
4. Доработку движка из раздела «Движок» ниже: изменение движка, тест
   компонента на фикстуре без имени кассы и строка в docs/SCHEMA.md.
5. QUESTIONS.md: что в доке неясно или противоречиво и что вы спросите
   у кассы. Где решили сами — какое решение и почему.
6. tools/verify.sh зелёный.

Правила:
- Живые вызовы к кассе не делать. Креды не нужны: секреты только ссылкой.
- Регистрироваться у кассы не нужно, достаточно публичной доки.
- Методы — только из брифа. Лишнее не добавлять.
- В src/ нет имени кассы: проверка гейта это ловит.
- Нужен .NET SDK 10. Сборка целится в net8.0, `global.json` просит SDK 10: на SDK 8 `dotnet test --solution` не запускается.
- Ожидания других кейсов и тестов не должны меняться.
- Можно пользоваться AI-агентами. Напишите в QUESTIONS.md, чем пользовались.
- Сдать: zip с git-репозиторием (git init сделайте сами, коммиты
  по смыслу, Conventional Commits).
- Время: рассчитано на 6–8 часов. Срок: пятница, 20 ноября 2026, до 23:59 по Москве.

## Бриф

Документация: https://docs.paysnapper.xyz/ — раздел Payment Methods.

Касса PaySnapper. Фирма `firm-a`. Секция `PaySnapper`. Выплаты нет. Берите основные id, без суффиксных вариантов.

Бенин, XOF:

| Код | Оператор | Id |
| --- | --- | --- |
| `P2P_PAYIN_PHONE_XOF_BEN_MOOV_XOF_BEN_MOOV` | MOOV | 836 |
| `P2P_PAYIN_PHONE_XOF_BEN_MTN_XOF_BEN_MTN` | MTN | 835 |
| `P2P_PAYIN_PHONE_XOF_BEN_CELTIIS_XOF_BEN_CELTIIS` | CELTIIS | 10007 |

Нигер, XOF:

| Код | Оператор | Id |
| --- | --- | --- |
| `P2P_PAYIN_PHONE_XOF_NER_AIRTEL_XOF_NER_AIRTEL` | AIRTEL | 9564 |
| `P2P_PAYIN_PHONE_XOF_NER_AMANA_XOF_NER_AMANA` | AMANA | 9562 |
| `P2P_PAYIN_PHONE_XOF_NER_ZAMANI_XOF_NER_ZAMANI` | ZAMANI | 9563 |

Кения, KES:

| Код | Оператор | Id |
| --- | --- | --- |
| `P2P_PAYIN_PHONE_KES_KEN_MPESASAFARICOM_KES_KEN_MPESASAFARICOM` | MPESASAFARICOM | 292 |
| `P2P_PAYIN_PHONE_KES_KEN_AIRTEL_KES_KEN_AIRTEL` | AIRTEL | 293 |

## Движок

Касса принимает в части валют только суммы с определённым шагом. Сейчас движок отправит любую. Доработайте движок, чтобы конфиг метода задавал это правило. Что делать с неподходящей суммой, решите сами и объясните в QUESTIONS.md.
