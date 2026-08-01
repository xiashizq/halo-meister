#!/usr/bin/env python3
"""Localize repeated bridge-not-responding exceptions across services."""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICES = ROOT / "src" / "HaloMeister.App" / "Services"
I18N = ROOT / "src" / "HaloMeister.App" / "Assets" / "i18n"

KEYS = {
    "en": {
        "bridge.error_not_responding": (
            "The in-game bridge is not responding. Install or repair it, "
            "restart the game, and load an offline campaign mission."
        ),
        "bridge.error_not_responding_restart": (
            "The in-game bridge is not responding. Restart the game and load an offline campaign mission."
        ),
        "bridge.error_not_responding_repair": (
            "The in-game bridge is not responding. Repair it, restart the game, "
            "and load an offline campaign mission."
        ),
        "bridge.error_scripting_not_responding": (
            "The in-game scripting bridge is not responding. Start or restart the game, then refresh the bridge status."
        ),
    },
    "zh-Hans": {
        "bridge.error_not_responding": (
            "游戏内 bridge 无响应。请安装或修复它，重启游戏，并加载离线战役任务。"
        ),
        "bridge.error_not_responding_restart": (
            "游戏内 bridge 无响应。请重启游戏并加载离线战役任务。"
        ),
        "bridge.error_not_responding_repair": (
            "游戏内 bridge 无响应。请修复它，重启游戏，并加载离线战役任务。"
        ),
        "bridge.error_scripting_not_responding": (
            "游戏内 scripting bridge 无响应。请启动或重启游戏，然后刷新 bridge 状态。"
        ),
    },
    "ja": {
        "bridge.error_not_responding": (
            "ゲーム内 bridge が応答していません。インストールまたは修復し、ゲームを再起動してオフラインキャンペーンミッションを読み込んでください。"
        ),
        "bridge.error_not_responding_restart": (
            "ゲーム内 bridge が応答していません。ゲームを再起動し、オフラインキャンペーンミッションを読み込んでください。"
        ),
        "bridge.error_not_responding_repair": (
            "ゲーム内 bridge が応答していません。修復し、ゲームを再起動してオフラインキャンペーンミッションを読み込んでください。"
        ),
        "bridge.error_scripting_not_responding": (
            "ゲーム内 scripting bridge が応答していません。ゲームを起動または再起動し、bridge 状態を更新してください。"
        ),
    },
    "ko": {
        "bridge.error_not_responding": (
            "인게임 bridge가 응답하지 않습니다. 설치 또는 복구한 뒤 게임을 다시 시작하고 오프라인 캠페인 미션을 로드하세요."
        ),
        "bridge.error_not_responding_restart": (
            "인게임 bridge가 응답하지 않습니다. 게임을 다시 시작하고 오프라인 캠페인 미션을 로드하세요."
        ),
        "bridge.error_not_responding_repair": (
            "인게임 bridge가 응답하지 않습니다. 복구한 뒤 게임을 다시 시작하고 오프라인 캠페인 미션을 로드하세요."
        ),
        "bridge.error_scripting_not_responding": (
            "인게임 scripting bridge가 응답하지 않습니다. 게임을 시작하거나 다시 시작한 뒤 bridge 상태를 새로 고치세요."
        ),
    },
}

REPLACEMENTS = [
    (
        re.compile(
            r'"The in-game bridge is not responding\. Install or repair it, "\s*\+\s*'
            r'"restart the game, and load an offline campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding")',
    ),
    (
        re.compile(
            r'"The in-game bridge is not responding\. Install or repair it, restart the game, and load an offline campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding")',
    ),
    (
        re.compile(
            r'"The in-game bridge is not responding\. Install or repair it, restart "\s*\+\s*'
            r'"the game, and load an offline campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding")',
    ),
    (
        re.compile(
            r'"The in-game bridge is not responding\. Install or repair it, restart the game, "\s*\+\s*'
            r'"and load a campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding")',
    ),
    (
        re.compile(
            r'"The in-game bridge is not responding\. Install or repair it, restart the game, and load a campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding")',
    ),
    (
        re.compile(
            r'"The in-game bridge is not responding\. Restart the game and load an offline campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding_restart")',
    ),
    (
        re.compile(
            r'"The in-game bridge is not responding\. Repair it, restart the game, "\s*\+\s*'
            r'"and load a campaign mission\."'
        ),
        'L.Get("bridge.error_not_responding_repair")',
    ),
    (
        re.compile(
            r'"The in-game scripting bridge is not responding\. Start or restart the game, then refresh the bridge status\."'
        ),
        'L.Get("bridge.error_scripting_not_responding")',
    ),
]

USING = "using HaloMeister.App.Localization;\n"


def ensure_using(text: str) -> str:
    if "using HaloMeister.App.Localization;" in text:
        return text
    match = re.search(r"(?:using [^;]+;\r?\n)+", text)
    if not match:
        return USING + text
    insert_at = match.end()
    return text[:insert_at] + USING + text[insert_at:]


def main() -> None:
    for lang, extras in KEYS.items():
        path = I18N / f"{lang}.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        data.update(extras)
        ordered = dict(sorted(data.items(), key=lambda kv: kv[0]))
        path.write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        print(f"{path.name}: {len(ordered)} keys")

    for path in sorted(SERVICES.glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        original = text
        for pattern, replacement in REPLACEMENTS:
            text = pattern.sub(replacement, text)
        if text != original:
            text = ensure_using(text)
            path.write_text(text, encoding="utf-8", newline="\n")
            print(f"patched {path.name}")


if __name__ == "__main__":
    main()
