#!/usr/bin/env python3
"""Rewrite {i18n:Loc key} to {i18n:Loc Key='key'} so WinUI won't treat dotted keys as types."""

from pathlib import Path
import re

app = Path(__file__).resolve().parents[1] / "src" / "HaloMeister.App"
files = [app / "MainWindow.xaml"] + sorted((app / "Pages").glob("*.xaml"))
pat = re.compile(r"\{i18n:Loc\s+([^}]+)\}")


def repl(match: re.Match[str]) -> str:
    inner = match.group(1).strip()
    if inner.startswith("Key="):
        # Normalize to quoted Key='...'
        value = inner[4:].strip().strip("'\"")
        return "{i18n:Loc Key='" + value + "'}"
    value = inner.strip().strip("'\"")
    return "{i18n:Loc Key='" + value + "'}"


total = 0
for path in files:
    text = path.read_text(encoding="utf-8")
    new_text, count = pat.subn(repl, text)
    if count:
        path.write_text(new_text, encoding="utf-8", newline="\n")
        total += count
        print(f"{path.name}: {count}")
print("total", total)
