"""Secret and personal-data gate over the tracked files. Run through tools/scan-secrets.sh.

Prints file:line and the rule, never the value it found. Exit 1 on any finding.
"""

import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCOPE = ['src', 'tests', 'integrations', 'docs', 'tools', 'appsettings.example.json']

# (c) The one file that may name a PEM private-key label: it derives the test
# key pair from a fixed seed at run time; no key text lives in the repository.
KEY_FILE = 'tests/CaseRunner/GoldenKeys.cs'

# (a) Public test cards the inputs need. A Luhn-valid number outside this list
# looks like a real card.
TEST_CARDS = {'4111111111111111', '4242424242424242', '5555555555554444'}

# (e) The neutral numbers GoldenInputs gives Russia and Kazakhstan, and the
# Vietnam fixture number with the same 901234567 tail.
TEST_PHONES = {'79001234567', '77011234567', '84901234567'}

# (b) Documentation networks (RFC 5737), loopback and "any": never a real host.
DOC_NETS = ('192.0.2.', '198.51.100.', '203.0.113.')

SENSITIVE = r'(?:api[_-]?key|secret|secret[_-]?key|token|access[_-]?token|bearer[_-]?token|password|private[_-]?key)'

# (d) A key-like field with a value of its own, plain or inside an escaped JSON
# string. Only a $secret: reference or a {template} may stand there: the value
# comes from configuration or from the case runner at run time.
FIELD = re.compile(
    r'(\\?")(?:[A-Za-z0-9_.-]*:)?(' + SENSITIVE + r')\1\s*:\s*\1([^"\\]*)\1',
    re.IGNORECASE)

# Digits glued to letters are part of a hash, a hex signature or an id, not a
# card or a phone of their own.
CARD = re.compile(r'(?<![0-9A-Za-z])[2-6][0-9]{12,18}(?![0-9A-Za-z])')
IPV4 = re.compile(r'(?<![0-9.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9.])')
PHONE = re.compile(r'(?<![0-9A-Za-z+])\+?[78][0-9]{10}(?![0-9A-Za-z])')
# Split in two so the scanner does not find itself.
PEM = re.compile('PRIVATE' + r'\s' + 'KEY')


def tracked():
    try:
        out = subprocess.run(
            ['git', 'ls-files', '-z', '--'] + SCOPE,
            cwd=ROOT,
            capture_output=True,
            check=True)
        names = [name for name in out.stdout.decode('utf-8').split('\0') if name]
        if names:
            return names
    except (OSError, subprocess.CalledProcessError):
        pass

    skip = {'bin', 'obj', '.git'}
    found = []
    for entry in SCOPE:
        path = os.path.join(ROOT, entry)
        if os.path.isfile(path):
            found.append(entry.replace('\\', '/'))
            continue
        if not os.path.isdir(path):
            continue
        for folder, dirs, files in os.walk(path):
            dirs[:] = [item for item in dirs if item not in skip]
            for file in files:
                full = os.path.join(folder, file)
                found.append(os.path.relpath(full, ROOT).replace('\\', '/'))
    return found


def luhn(digits):
    total = 0
    for index, char in enumerate(reversed(digits)):
        digit = int(char)
        if index % 2 == 1:
            digit *= 2
            if digit > 9:
                digit -= 9
        total += digit
    return total % 10 == 0


def stamp(digits):
    """A yyyyMMddHHmmss moment (the "datetime" timestamp format): fourteen digits that read as a date, not a card."""
    if len(digits) != 14 or not digits.startswith(('19', '20')):
        return False
    month, day, hour, minute, second = (int(digits[i:i + 2]) for i in (4, 6, 8, 10, 12))
    return 1 <= month <= 12 and 1 <= day <= 31 and hour < 24 and minute < 60 and second < 60


def engine_pool():
    """Payer addresses shipped with the engine, when a pool file exists."""
    path = os.path.join(ROOT, 'src', 'PayEngine', 'appsettings.json')
    if not os.path.isfile(path):
        return set()
    with open(path, encoding='utf-8-sig') as handle:
        pools = json.load(handle).get('ProviderEngine', {}).get('IpPools', {})
    return {address for addresses in pools.values() for address in addresses}


def allowed_ip(text, pool):
    parts = [int(part) for part in text.split('.')]
    if any(part > 255 for part in parts):
        return True
    return parts[0] == 127 or text == '0.0.0.0' or text.startswith(DOC_NETS) or text in pool


def definition_constants():
    """Every static string value of the definitions in integrations/.

    Definition constants are product data: the fields agreed with the cashier
    (a fixed customer phone, a fixed address) that the engine sends as written.
    A recorded wire that repeats such a value repeats the definition, not
    someone's personal data, so the phone and address rules let it pass.
    """
    constants = set()

    def walk(node):
        if isinstance(node, dict):
            for child in node.values():
                walk(child)
        elif isinstance(node, list):
            for child in node:
                walk(child)
        elif isinstance(node, str):
            constants.add(node)

    for name in tracked():
        if name.startswith('integrations/') and name.endswith('.json'):
            with open(os.path.join(ROOT, name), encoding='utf-8-sig') as handle:
                walk(json.load(handle))
    return constants


def main():
    pool = engine_pool()
    constants = definition_constants()
    findings = []
    texts = {}

    for name in tracked():
        try:
            with open(os.path.join(ROOT, name), encoding='utf-8') as handle:
                texts[name] = handle.read()
        except (UnicodeDecodeError, FileNotFoundError, IsADirectoryError):
            continue

    for name, text in texts.items():

        strict = name.startswith(('tests/', 'integrations/'))

        for number, line in enumerate(text.splitlines(), 1):
            where = f'{name}:{number}'

            for match in FIELD.finditer(line):
                value = match.group(3)
                literal = value and not value.startswith('$secret:') and not value.startswith('{')
                # src и пример настроек - прежнее правило: заметна только
                # строка от восьми знаков. tests и integrations - любая.
                if literal and (strict or len(value) >= 8):
                    findings.append(f'{where}: (d) field "{match.group(2)}" holds a literal value')

            if PEM.search(line) and name != KEY_FILE:
                findings.append(f'{where}: (c) private key label outside {KEY_FILE}')

            for match in CARD.finditer(line):
                if match.group(0) not in TEST_CARDS and luhn(match.group(0)) and not stamp(match.group(0)):
                    findings.append(f'{where}: (a) Luhn-valid card-like number')

            for match in IPV4.finditer(line):
                if not allowed_ip(match.group(0), pool) and match.group(0) not in constants:
                    findings.append(f'{where}: (b) IPv4 address outside documentation networks, the engine pool and the definitions')

            if name.startswith('tests/'):
                for match in PHONE.finditer(line):
                    if match.group(0).lstrip('+') not in TEST_PHONES and match.group(0) not in constants:
                        findings.append(f'{where}: (e) Russian-format phone number')

    for finding in findings:
        print(finding, file=sys.stderr)

    if findings:
        print(f'scan-secrets: {len(findings)} finding(s)', file=sys.stderr)
        return 1

    return 0


if __name__ == '__main__':
    sys.exit(main())
