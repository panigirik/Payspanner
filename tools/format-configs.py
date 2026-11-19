# Выравнивает integrations/*.json и проверяет группы методов.
#
# Полоса пишется одной строкой: [id, min, max] или [id]. Короткий список
# полос остаётся на одной строке, длинный — по полосе на строку.
#
# Сортирует то, от чего код метода не зависит: группы (валюта, страна,
# iso, тип реквизита), имена операторов, Push и id в Body.
# Порядок полос не меняет. Первый id оператора держит чистое имя
# в коде метода, остальные дописывают номер. Тело запроса, Outcome
# и StatusMap тоже не сортируются: от их порядка зависят подпись и разбор.
#
# Base — копия кассы: имя секции другого определения из integrations/*.json.
# База есть, это не сама копия, у базы нет своей Base, код у копии свой.
# Drop — пути базы, которые копия не берёт: только вместе с Base, каждый
# путь есть у базы и не ведёт внутрь списка.
#
#   python tools/format-configs.py
#   python tools/format-configs.py --check
#   python tools/format-configs.py integrations/fake.json

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WIDTH = 100
DETAILS = {
    "CARD": "CARD",
    "PHONE": "PHONE",
    "BANKACCOUNT": "BANK_ACCOUNT",
}
FAMILIES = {
    ("MOBCOM", "PHONE"): "MOBCOM_PHONE",
    ("ECOMM", "CARD"): "ECOMM_CARD",
}


def main() -> int:
    args = sys.argv[1:]
    check = "--check" in args
    paths = [Path(arg) for arg in args if not arg.startswith("-")]
    if not paths:
        paths = sorted((ROOT / "integrations").glob("*.json"))

    problems = []
    dirty = []
    known = known_providers(paths)
    for path in paths:
        file_problems, changed, before, after = format_file(path, write=not check, known=known)
        problems.extend(file_problems)
        if changed:
            dirty.append((path, before, after))

    for path, before, after in dirty:
        print(f"{rel(path)}: {before} -> {after}")

    if check and dirty:
        problems.append(f"{len(dirty)} file(s) are not formatted")

    for problem in problems:
        print(problem)

    if not problems and not dirty:
        print(f"ok, {len(paths)} file(s)")

    return 1 if problems else 0


def known_providers(paths: list[Path]) -> dict:
    known = {}
    for path in sorted(set(sorted((ROOT / "integrations").glob("*.json"))) | set(paths)):
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        providers = data.get("Providers") if isinstance(data, dict) else None
        if not isinstance(providers, dict):
            continue
        for name, provider in providers.items():
            if isinstance(provider, dict):
                known[name.lower()] = (name, provider)
    return known


def format_file(path: Path, write: bool, known: dict) -> tuple[list[str], bool, int, int]:
    raw = path.read_bytes()
    newline = "\r\n" if raw.count(b"\r\n") > raw.count(b"\n") - raw.count(b"\r\n") else "\n"
    text = raw.decode("utf-8-sig")
    problems = []

    try:
        data = json.loads(text)
    except json.JSONDecodeError as error:
        return [f"{rel(path)}:{error.lineno}: {error.msg}"], False, 0, 0

    if not isinstance(data, dict):
        return [f"{rel(path)}: root is not an object"], False, 0, 0

    arrange(data, problems, rel(path), known)
    rendered = render(data) + "\n"
    current = text.replace("\r\n", "\n").replace("\r", "\n")
    if not current.endswith("\n"):
        current += "\n"
    changed = current != rendered
    before = current.count("\n")
    after = rendered.count("\n")

    if write and changed:
        written = rendered.replace("\n", newline) if newline != "\n" else rendered
        path.write_bytes(written.encode("utf-8"))

    return problems, changed, before, after


def arrange(data: dict, problems: list[str], path: str, known: dict) -> None:
    providers = data.get("Providers")
    if not isinstance(providers, dict):
        problems.append(f"{path}: Providers is missing")
        return

    for name, provider in providers.items():
        if not isinstance(provider, dict):
            problems.append(f"{path}: Providers:{name} is not an object")
            continue
        check_base(name, provider, known, problems, f"{path}: Providers:{name}:Base")
        groups = provider.get("MethodGroups")
        if groups is None:
            continue
        provider["MethodGroups"] = arrange_groups(groups, problems, f"{path}: Providers:{name}:MethodGroups")


OWN = {"base", "drop", "code", "displayname", "enabled", "firms", "firm", "paymentmethods",
       "methodgroups", "bankcodes", "banknames", "mobileoperators"}


def lookup(node, key: str):
    if not isinstance(node, dict):
        return None
    for name, value in node.items():
        if name.lower() == key.lower():
            return value
    return None


def check_base(name: str, provider: dict, known: dict, problems: list[str], path: str) -> None:
    if "Base" not in provider:
        if "Drop" in provider:
            problems.append(f"{path[:-len('Base')]}Drop needs Base: there is nothing to drop from")
        return
    base = provider["Base"]
    if not isinstance(base, str) or not base.strip():
        problems.append(f"{path} must be the section name of one definition")
        return
    base = base.strip()
    if base.lower() == name.lower():
        problems.append(f"{path} '{base}' names the definition itself")
        return
    found = known.get(base.lower())
    if found is None or not text(found[1].get("Code")):
        problems.append(f"{path} '{base}' is not a provider definition")
        return
    further = found[1].get("Base")
    if further is not None:
        if isinstance(further, str) and further.strip().lower() == name.lower():
            problems.append(f"{path} '{base}' is a copy of {name} itself: the two names make a cycle")
        else:
            problems.append(f"{path} '{base}' is itself a copy of '{further}': one level only")
    if not text(provider.get("Code")):
        problems.append(f"{path}: a copy writes its own Code")
    check_drop(provider.get("Drop"), found[1], problems, path[:-len("Base")] + "Drop")


def check_drop(drop, base: dict, problems: list[str], path: str) -> None:
    if drop is None:
        return
    if not isinstance(drop, list):
        problems.append(f"{path} must be a list of paths of the base")
        return
    for index, item in enumerate(drop):
        item_path = f"{path}:{index}"
        if not isinstance(item, str) or not item.strip():
            problems.append(f"{item_path} must be a path of the base")
            continue
        segments = item.strip().split(":")
        if any(not segment.strip() for segment in segments):
            problems.append(f"{item_path} '{item}' has an empty segment")
            continue
        if segments[0].lower() in OWN:
            problems.append(f"{item_path} '{item}' is under {segments[0]}, which a copy never takes from its base")
            continue
        node = base
        for segment in segments:
            if isinstance(node, list):
                node = None
                break
            node = lookup(node, segment)
        if node is None:
            problems.append(f"{item_path} '{item}' is not in the base, or goes inside a list")


def arrange_groups(groups, problems: list[str], path: str) -> list:
    if not isinstance(groups, list):
        problems.append(f"{path} is not a list")
        return groups

    codes = {}
    arranged = []
    for index, group in enumerate(groups):
        group_path = f"{path}:{index}"
        if not isinstance(group, dict):
            problems.append(f"{group_path} is not an object")
            arranged.append(group)
            continue
        arranged.append(arrange_group(group, problems, group_path, codes))

    arranged.sort(key=lambda group: group_sort_key(group) if isinstance(group, dict) else ("", "", "", ""))
    return arranged


def arrange_group(group: dict, problems: list[str], path: str, codes: dict) -> dict:
    currency = text(group.get("Currency"))
    country = text(group.get("Country"))
    iso = text(group.get("Iso"))
    details = text(group.get("DetailsType"))
    token = DETAILS.get(details.upper())
    if token is None:
        problems.append(f"{path}: DetailsType '{details}' is unknown")
    family = text(group.get("Family"))
    if token is not None and family:
        qualified = FAMILIES.get((family, token))
        if qualified is None:
            problems.append(f"{path}: Family '{family}' is not a name in front of {token}")
            token = None
        else:
            token = qualified

    if not currency:
        problems.append(f"{path}: Currency is required")
    if not country:
        problems.append(f"{path}: Country is required")
    if not iso:
        problems.append(f"{path}: Iso is required")

    seen = set()
    operators = group.get("Operators")
    if not isinstance(operators, dict) or not operators:
        problems.append(f"{path}: Operators is empty")
        operators = {}

    ordered = {}
    for slug in sorted(operators, key=str):
        bands = operators[slug]
        op_path = f"{path}:Operators:{slug}"
        if not slug or not slug.isascii() or not slug.isalnum():
            problems.append(f"{op_path}: name must be letters and digits")
        if not isinstance(bands, list) or not bands:
            problems.append(f"{op_path}: has no provider ids")
            ordered[slug] = bands
            continue
        ordered[slug] = bands
        for band_index, band in enumerate(bands):
            band_path = f"{op_path}:{band_index}"
            parsed = read_band(band, problems, band_path)
            if parsed is None or token is None:
                continue
            provider_id, _, _ = parsed
            if provider_id in seen:
                problems.append(f"{band_path}: provider id {provider_id} is repeated in this group")
            seen.add(provider_id)
            bank = slug if band_index == 0 else f"{slug}{provider_id}"
            code = f"P2P_PAYIN_{token}_{currency}_{iso}_{bank}_{currency}_{iso}_{bank}"
            if code in codes:
                problems.append(f"{band_path}: '{code}' is already a payment method ({codes[code]})")
            else:
                codes[code] = band_path

    group["Operators"] = ordered
    check_ids(group.get("Push"), seen, problems, f"{path}:Push")
    check_body(group.get("Body"), seen, problems, f"{path}:Body")

    if isinstance(group.get("Push"), list) and all(is_number(item) for item in group["Push"]):
        group["Push"] = sorted(group["Push"], key=lambda item: (int(item), item))
    body = group.get("Body")
    if isinstance(body, dict):
        group["Body"] = dict(sorted(body.items(), key=lambda item: id_key(item[0])))

    return group


def read_band(band, problems: list[str], path: str):
    if not isinstance(band, list) or not 1 <= len(band) <= 3 or not all(is_number(cell) for cell in band):
        problems.append(f"{path}: is id, or id with a minimum and a maximum")
        return None

    provider_id = band[0]
    if isinstance(provider_id, float) and not provider_id.is_integer() or provider_id <= 0:
        problems.append(f"{path}: provider id must be a whole number above zero")
        return None

    minimum = band[1] if len(band) > 1 else 0
    maximum = band[2] if len(band) > 2 else 0
    if minimum < 0 or maximum < 0:
        problems.append(f"{path}: amount must be zero or more")
        return None
    if maximum > 0 and minimum > maximum:
        problems.append(f"{path}: minimum is above maximum")
        return None
    return int(provider_id), minimum, maximum


def check_ids(values, seen: set, problems: list[str], path: str) -> None:
    if values is None:
        return
    if not isinstance(values, list):
        problems.append(f"{path} is not a list")
        return
    for value in values:
        if not is_number(value) or int(value) not in seen:
            problems.append(f"{path}: '{value}' is not a provider id of this group")


def check_body(values, seen: set, problems: list[str], path: str) -> None:
    if values is None:
        return
    if not isinstance(values, dict):
        problems.append(f"{path} is not an object")
        return
    for key in values:
        if key not in seen and not (key.isdigit() and int(key) in seen):
            problems.append(f"{path}: '{key}' is not a provider id of this group")


def group_sort_key(group: dict) -> tuple:
    return (
        text(group.get("Currency")),
        text(group.get("Country")),
        text(group.get("Iso")),
        text(group.get("DetailsType")),
    )


def id_key(key: str) -> tuple:
    return (0, int(key)) if key.isdigit() else (1, key)


def render(value, level: int = 0) -> str:
    if isinstance(value, dict):
        return render_object(value, level)
    if isinstance(value, list):
        return render_list(value, level)
    return json.dumps(value, ensure_ascii=False)


def render_object(value: dict, level: int) -> str:
    if not value:
        return "{}"
    pad = "  " * level
    inner = "  " * (level + 1)
    lines = ["{"]
    items = list(value.items())
    for index, (key, item) in enumerate(items):
        comma = "," if index < len(items) - 1 else ""
        lines.append(f"{inner}{json.dumps(key, ensure_ascii=False)}: {render(item, level + 1)}{comma}")
    lines.append(f"{pad}}}")
    return "\n".join(lines)


def render_list(value: list, level: int) -> str:
    if not value:
        return "[]"
    if all(is_number(item) for item in value):
        return json.dumps(value, ensure_ascii=False)
    if all(is_band(item) for item in value):
        parts = [json.dumps(item, ensure_ascii=False) for item in value]
        one = "[" + ", ".join(parts) + "]"
        if len(one) <= WIDTH:
            return one
        return render_lines(parts, level)
    return render_lines([render(item, level + 1) for item in value], level)


def render_lines(parts: list[str], level: int) -> str:
    pad = "  " * level
    inner = "  " * (level + 1)
    lines = ["["]
    for index, part in enumerate(parts):
        comma = "," if index < len(parts) - 1 else ""
        piece = part.split("\n")
        piece[0] = inner + piece[0]
        piece[-1] += comma
        lines.append("\n".join(piece))
    lines.append(pad + "]")
    return "\n".join(lines)


def is_band(value) -> bool:
    return isinstance(value, list) and bool(value) and all(is_number(item) for item in value)


def is_number(value) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def text(value) -> str:
    return value.strip() if isinstance(value, str) else ""


def rel(path: Path) -> str:
    try:
        return str(path.resolve().relative_to(ROOT))
    except ValueError:
        return str(path)


if __name__ == "__main__":
    sys.exit(main())
