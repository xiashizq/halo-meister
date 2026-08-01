#!/usr/bin/env python3
from pathlib import Path
import re

app = Path(__file__).resolve().parents[1] / "src" / "HaloMeister.App"
files = [app / "MainWindow.xaml"] + sorted((app / "Pages").glob("*.xaml"))
pat = re.compile(r"(\{i18n:Loc [^}]+\})\"\"")
fixed = 0
for path in files:
    text = path.read_text(encoding="utf-8")
    new_text, count = pat.subn(r'\1"', text)
    if count:
        path.write_text(new_text, encoding="utf-8", newline="\n")
        fixed += count
        print(f"{path.name}: {count}")
print("total fixes", fixed)

sample = (app / "MainWindow.xaml").read_text(encoding="utf-8")
print("bad remaining", len(re.findall(r'\{i18n:Loc [^}]+\}""', sample)))
for line in sample.splitlines():
    if "i18n:Loc" in line:
        print(line.strip())
        break
