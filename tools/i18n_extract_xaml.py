#!/usr/bin/env python3
"""Extract localizable XAML attribute strings and rewrite them to {i18n:Loc key}."""

from __future__ import annotations

import json
import re
import hashlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "HaloMeister.App"
OUT_DIR = APP / "Assets" / "i18n"

ATTRS = (
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
)

# Skip brand names and purely technical tokens.
SKIP_EXACT = {
    "Halo Meister",
    "HM",
    "OK",
    "PID",
    "UE4SS",
    "PlayFab",
    "WGS",
    "True",
    "False",
    "true",
    "false",
    "…",
    "...",
    "—",
    "-",
    "*",
}

SKIP_PREFIXES = (
    "ms-appx:",
    "http",
    "#",
    "{",
    "&#",
)

ATTR_RE = re.compile(
    r"""(?P<attr>(?:ToolTipService\.ToolTip|AutomationProperties\.(?:Name|HelpText)|"""
    r"""Text|Content|Header|Title|Label|PlaceholderText|Message|OffContent|OnContent))"""
    r"""\s*=\s*"(?P<value>[^"]*)\""""
)


def slug(text: str, max_len: int = 48) -> str:
    s = text.lower()
    s = s.replace("&amp;", " and ").replace("&lt;", " ").replace("&gt;", " ")
    s = re.sub(r"[^a-z0-9]+", "_", s).strip("_")
    if not s:
        s = "text"
    if len(s) > max_len:
        digest = hashlib.sha1(text.encode("utf-8")).hexdigest()[:6]
        s = s[: max_len - 7].rstrip("_") + "_" + digest
    return s


def should_skip(value: str) -> bool:
    v = value.strip()
    if not v:
        return True
    if v in SKIP_EXACT:
        return True
    if any(v.startswith(p) for p in SKIP_PREFIXES):
        return True
    # Pure numbers / symbols
    if re.fullmatch(r"[\d\s\.\,\:\;\-\+\*/\\]+", v):
        return True
    # No letters at all
    if not re.search(r"[A-Za-z]", v):
        return True
    # Binding / already localized
    if v.startswith("{i18n:") or v.startswith("{x:") or v.startswith("{Binding") or v.startswith("{x:Bind"):
        return True
    return False


def unescape_xaml(value: str) -> str:
    return (
        value.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", '"')
        .replace("&#x0a;", "\n")
        .replace("&#10;", "\n")
    )


def escape_xaml_attr(value: str) -> str:
    return (
        value.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
    )


def page_prefix(path: Path) -> str:
    name = path.stem
    if name == "MainWindow":
        return "shell"
    if name.endswith("Page"):
        name = name[: -len("Page")]
    # camel to snake-ish
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name).lower()
    return s


def ensure_xmlns(content: str) -> str:
    if "xmlns:i18n=" in content:
        return content
    # Insert after xmlns:x declaration if present, else after first xmlns=
    m = re.search(r'\sxmlns:x="[^"]+"', content)
    insert = '\n    xmlns:i18n="using:HaloMeister.App.Localization"'
    if m:
        pos = m.end()
        return content[:pos] + insert + content[pos:]
    m = re.search(r'\sxmlns="[^"]+"', content)
    if m:
        pos = m.end()
        return content[:pos] + insert + content[pos:]
    return content


def process_file(path: Path, catalog: dict[str, str], key_counts: dict[str, int]) -> str:
    text = path.read_text(encoding="utf-8")
    prefix = page_prefix(path)
    changed = False

    def repl(match: re.Match[str]) -> str:
        nonlocal changed
        attr = match.group("attr")
        raw = match.group("value")
        if should_skip(raw):
            return match.group(0)

        english = unescape_xaml(raw)
        base_key = f"{prefix}.{slug(english)}"
        key = base_key
        # Deduplicate collisions with different English
        n = 1
        while key in catalog and catalog[key] != english:
            n += 1
            key = f"{base_key}_{n}"

        catalog[key] = english
        key_counts[key] = key_counts.get(key, 0) + 1
        changed = True
        return f"{attr}=\"{{i18n:Loc Key='{key}'}}\""

    new_text = ATTR_RE.sub(repl, text)
    if changed:
        new_text = ensure_xmlns(new_text)
    return new_text


def main() -> None:
    xaml_files = [APP / "MainWindow.xaml"]
    xaml_files += sorted((APP / "Pages").glob("*.xaml"))

    catalog: dict[str, str] = {}
    key_counts: dict[str, int] = {}

    for path in xaml_files:
        rewritten = process_file(path, catalog, key_counts)
        path.write_text(rewritten, encoding="utf-8", newline="\n")
        print(f"updated {path.relative_to(ROOT)} ({sum(1 for k in catalog if k.startswith(page_prefix(path)))} keys so far total {len(catalog)})")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    # Stable key order
    ordered = dict(sorted(catalog.items(), key=lambda kv: kv[0]))
    en_path = OUT_DIR / "en.json"
    en_path.write_text(
        json.dumps(ordered, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"wrote {en_path} with {len(ordered)} keys")


if __name__ == "__main__":
    main()
