#!/usr/bin/env python3
"""Find Loc bindings on attributes that are unlikely to be localizable strings."""

from pathlib import Path
import re

ALLOW = {
    "Text",
    "Content",
    "Header",
    "Title",
    "Label",
    "PlaceholderText",
    "Message",
    "OffContent",
    "OnContent",
    "ToolTipService.ToolTip",
    "AutomationProperties.Name",
    "AutomationProperties.HelpText",
}

root = Path(__file__).resolve().parents[1] / "src" / "HaloMeister.App"
files = [root / "MainWindow.xaml"] + sorted((root / "Pages").glob("*.xaml"))
pat = re.compile(
    r"(?P<attr>[\w.]+)\s*=\s*\"\{i18n:Loc Key='(?P<key>[^']+)'\}\""
)

found = False
for path in files:
    text = path.read_text(encoding="utf-8")
    for match in pat.finditer(text):
        attr = match.group("attr")
        if attr not in ALLOW:
            found = True
            print(f"{path.name}: {attr} = {match.group('key')}")

if not found:
    print("ok: no suspicious Loc attributes")
